using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LiveAdConnect;
sealed class AuthError(string message,string code="auth_error") : Exception(message) {public string Code {get;}=code;}
record CallbackResult(string Code,string ClientId);
static class OAuthProtocol
{
    public static string Random()=>Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier)=>Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static bool HasUsage(string scopes)=>scopes.Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct");
    public static CallbackResult ParseCallback(string requestLine,string expectedState,string? savedClient)
    {
        var parts=requestLine.Split(' ');
        if(parts.Length!=3 || parts[0]!="GET" || !parts[1].StartsWith("/auth/callback?",StringComparison.Ordinal))throw new AuthError("不是本次授权回调。");
        if(parts[1].Length>16000)throw new AuthError("授权回调超长。");
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var pair in parts[1][(parts[1].IndexOf('?')+1)..].Split('&'))
        {
            var p=pair.Split('=',2);var key=Uri.UnescapeDataString(p[0]);var value=p.Length==2?Uri.UnescapeDataString(p[1].Replace('+',' ')):"";
            if(!fields.TryAdd(key,value))throw new AuthError("授权回调包含重复参数。");
        }
        var state=fields.GetValueOrDefault("state","");
        if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state),Encoding.UTF8.GetBytes(expectedState)))throw new AuthError("授权校验不匹配，已拒绝回调。");
        if(fields.ContainsKey("error"))throw new AuthError("你未完成或未同意授权，可取消后重新连接。","authorization_denied");
        var client=fields.GetValueOrDefault("client_id",savedClient??"");
        if(client=="dynamic_agent_client" || !Regex.IsMatch(client,@"^[A-Za-z0-9_-]{1,200}$") || (savedClient!=null && client!=savedClient))throw new AuthError("授权应用标识缺失或不匹配。");
        if(!fields.TryGetValue("code",out var code)||code.Length==0)throw new AuthError("未收到授权码。");
        return new(code,client);
    }
}
sealed class Credentials
{
    public string ClientId {get;set;}="";
    public string AccessToken {get;set;}="";
    public string IdToken {get;set;}="";
    public string RefreshToken {get;set;}="";
    public Credentials? PendingRotation {get;set;}
    public string Subject {get;set;}="";
    public string Email {get;set;}="";
    public string Scopes {get;set;}="";
    public DateTime ExpiresAt {get;set;}
    public bool CanUse=>PendingRotation==null && AccessToken.Length>0 && ExpiresAt>DateTime.UtcNow.AddSeconds(60) && OAuthProtocol.HasUsage(Scopes);
}
sealed class CredentialStore
{
    public string DirectoryPath {get;}
    readonly string path;
    static readonly byte[] entropy=Encoding.UTF8.GetBytes("LiveAdConnect.oauth.v1");
    public CredentialStore(string path){DirectoryPath=path;this.path=Path.Combine(path,"credentials.bin");}
    public Credentials? Load()
    {
        if(!File.Exists(path))return null;
        try
        {
            var plain=ProtectedData.Unprotect(File.ReadAllBytes(path),entropy,DataProtectionScope.CurrentUser);
            try{return JsonSerializer.Deserialize<Credentials>(plain)??throw new Exception();}finally{CryptographicOperations.ZeroMemory(plain);}
        }catch{throw new AuthError("本机授权记录无法解密，请使用原Windows账号；不要复制授权文件到另一台电脑。", "storage_error");}
    }
    public void Save(Credentials c)
    {
        Directory.CreateDirectory(DirectoryPath);var plain=JsonSerializer.SerializeToUtf8Bytes(c);
        try
        {
            var cipher=ProtectedData.Protect(plain,entropy,DataProtectionScope.CurrentUser);var tmp=path+".tmp";File.WriteAllBytes(tmp,cipher);
            if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
        }finally{CryptographicOperations.ZeroMemory(plain);}
    }
    public string HostId()
    {
        Directory.CreateDirectory(DirectoryPath);var hostPath=Path.Combine(DirectoryPath,"host-id.txt");
        if(File.Exists(hostPath)){var s=File.ReadAllText(hostPath).Trim();if(s.StartsWith("urn:uuid:") && Guid.TryParse(s[9..],out _))return s;throw new AuthError("设备标识无效，请恢复原文件。");}
        var id="urn:uuid:"+Guid.NewGuid();File.WriteAllText(hostPath,id);return id;
    }
}
record VerifiedIdentity(string Subject,string Email);
static class IdentityVerifier
{
    static readonly ConfigurationManager<OpenIdConnectConfiguration> Discovery=new("https://auth.openai.com/.well-known/openid-configuration",new OpenIdConnectConfigurationRetriever());
    public static async Task<VerifiedIdentity> VerifyOnline(string token,string audience,string? nonce,CancellationToken ct)
    {
        var config=await Discovery.GetConfigurationAsync(ct);
        if(config.Issuer!="https://auth.openai.com")throw new AuthError("官方身份服务标识不匹配。");
        return Verify(token,audience,nonce,config.SigningKeys);
    }
    public static VerifiedIdentity Verify(string token,string audience,string? nonce,IEnumerable<SecurityKey> keys)
    {
        try
        {
            var handler=new JwtSecurityTokenHandler{MapInboundClaims=false};
            var principal=handler.ValidateToken(token,new TokenValidationParameters{
                ValidateIssuer=true,ValidIssuer="https://auth.openai.com",ValidateAudience=true,ValidAudience=audience,
                ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(60),RequireExpirationTime=true,RequireSignedTokens=true,
                ValidateIssuerSigningKey=true,IssuerSigningKeys=keys,ValidAlgorithms=[SecurityAlgorithms.RsaSha256]},out _);
            var actualNonce=principal.FindFirst("nonce")?.Value??"";
            if(nonce!=null && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actualNonce),Encoding.UTF8.GetBytes(nonce)))throw new Exception();
            var sub=principal.FindFirst("sub")?.Value;if(string.IsNullOrEmpty(sub))throw new Exception();
            return new(sub,principal.FindFirst("email")?.Value??principal.FindFirst("name")?.Value??"已授权账号");
        }catch{throw new AuthError("身份签名、有效期或授权随机校验未通过，未保存登录。","invalid_identity");}
    }
}
sealed class AuthClient : IDisposable
{
    public static readonly HttpClient Http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60),MaxResponseContentBufferSize=2*1024*1024};
    readonly CredentialStore store;
    public Credentials? Session {get;private set;}
    public string StartupError {get;private set;}="";
    public AuthClient(){store=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveAdConnect","auth"));try{Session=store.Load();}catch(AuthError e){StartupError=e.Message;}}
    public async Task Login(Action<string> progress,CancellationToken ct)
    {
        var host=store.HostId();var verifier=OAuthProtocol.Random();var state=OAuthProtocol.Random();var nonce=OAuthProtocol.Random();
        var old=Session;
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var port=((IPEndPoint)listener.LocalEndpoint).Port;var redirect=$"http://127.0.0.1:{port}/auth/callback";
            var fields=new Dictionary<string,string>{["client_id"]=old?.ClientId??"dynamic_agent_client",["ext_agent_host_id"]=host,
                ["response_type"]="code",["redirect_uri"]=redirect,["scope"]="openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
                ["resource"]="https://api.openai.com/v1",["state"]=state,["nonce"]=nonce,["code_challenge_method"]="S256",["code_challenge"]=OAuthProtocol.Challenge(verifier)};
            if(old==null)fields["agent_name_hint"]="Live Ad Companion";
            else if(old.IdToken.Length>0)fields["id_token_hint"]=old.IdToken;
            var uri="https://auth.openai.com/api/accounts/authorize?"+string.Join("&",fields.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
            Process.Start(new ProcessStartInfo(uri){UseShellExecute=true});
            progress("请在官方浏览器页面完成登录和额度授权；此窗口正在等待返回。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var callback=await WaitCallback(listener,port,state,old?.ClientId,timeout.Token);
            // 先保留正式client注册；授权码失效时重试复用注册，不创建重复应用。
            if(old==null){Session=new(){ClientId=callback.ClientId};store.Save(Session);}
            progress("正在交换授权并验证官方身份签名…");
            using var response=await Http.PostAsync("https://auth.openai.com/api/accounts/oauth/token",new FormUrlEncodedContent(new Dictionary<string,string>{
                ["grant_type"]="authorization_code",["client_id"]=callback.ClientId,["code"]=callback.Code,["code_verifier"]=verifier,["redirect_uri"]=redirect,["resource"]="https://api.openai.com/v1"}),ct);
            if(!response.IsSuccessStatusCode)throw new AuthError($"官方授权交换未完成（HTTP {(int)response.StatusCode}）。请重新连接；已保留应用注册。","token_exchange_failed");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var root=json.RootElement;
            string Str(string name)=>root.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
            var idToken=Str("id_token");var access=Str("access_token");var scope=Str("scope");
            if(idToken.Length==0||access.Length==0||!root.TryGetProperty("expires_in",out var exp)||!exp.TryGetInt32(out var seconds)||seconds<=0||seconds>604800)throw new AuthError("官方返回的授权字段不完整，未保存新凭据。");
            var identity=await IdentityVerifier.VerifyOnline(idToken,callback.ClientId,nonce,ct);
            if(old?.Subject.Length>0&&old.Subject!=identity.Subject)throw new AuthError("返回账号与原注册不一致，未替换原账号。");
            var next=new Credentials{ClientId=callback.ClientId,AccessToken=access,IdToken=idToken,RefreshToken=Str("refresh_token"),Subject=identity.Subject,Email=identity.Email,Scopes=scope,ExpiresAt=DateTime.UtcNow.AddSeconds(seconds)};
            ct.ThrowIfCancellationRequested();
            store.Save(next);Session=next;
            if(!OAuthProtocol.HasUsage(scope))throw new AuthError("身份已登录，但未获得ChatGPT套餐使用权限。请在官方设置中授权后重新连接。","usage_not_granted");
            progress("已连接ChatGPT并取得套餐使用权限。下一步请选择模型并发送示例。");
        }
        finally{listener.Stop();}
    }
    static async Task<CallbackResult> WaitCallback(TcpListener listener,int port,string state,string? client,CancellationToken ct)
    {
        while(true)
        {
            using var connection=await listener.AcceptTcpClientAsync(ct);using var stream=connection.GetStream();
            using var requestTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var buffer=new byte[1024];var text=new StringBuilder();
                while(!text.ToString().Contains("\r\n\r\n",StringComparison.Ordinal))
                {
                    var n=await stream.ReadAsync(buffer,requestTimeout.Token);if(n==0)throw new AuthError("不完整回调");text.Append(Encoding.ASCII.GetString(buffer,0,n));if(text.Length>16384)throw new AuthError("回调请求超长");
                }
                var lines=text.ToString().Split("\r\n");
                if(!lines.Any(l=>l.Equals($"Host: 127.0.0.1:{port}",StringComparison.OrdinalIgnoreCase)))throw new AuthError("回调主机不匹配");
                var result=OAuthProtocol.ParseCallback(lines[0],state,client);
                await Reply(stream,"已收到授权返回，请回到 Live Ad Companion。正在验证身份，是否成功以工具状态为准。",ct);
                return result;
            }
            catch(AuthError e)
            {
                try{await Reply(stream,"此请求未通过本次授权检查。请回到工具查看状态。",ct);}catch{}
                if(e.Code=="authorization_denied")throw;
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested) { /* 未完整提交的本机连接不终止整个登录等待。 */ }
            catch(IOException) { }
        }
    }
    static async Task Reply(Stream stream,string message,CancellationToken ct)
    {
        var body=Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Live Ad Companion</title><p>"+message+"</p>");
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header,ct);await stream.WriteAsync(body,ct);
    }
    public void Forget()
    {
        if(Session==null)return;
        Session=new(){ClientId=Session.ClientId,Subject=Session.Subject,Email=Session.Email};store.Save(Session);
    }
    public async Task<Credentials> Authorized(CancellationToken ct)
    {
        if(Session?.CanUse==true)return Session;
        if(Session?.PendingRotation!=null)throw new AuthError("上次授权续期未完成身份验证，请重新连接ChatGPT。");
        if(Session==null || Session.RefreshToken.Length==0)throw new AuthError("授权已到期或尚未连接，请点击连接ChatGPT。");
        var old=Session;
        // 新版只有一个运行实例，且所有AI/登录由同一操作锁串行调度。
        using var r=await Http.PostAsync("https://auth.openai.com/api/accounts/oauth/token",new FormUrlEncodedContent(new Dictionary<string,string>{["grant_type"]="refresh_token",["client_id"]=old.ClientId,["refresh_token"]=old.RefreshToken,["resource"]="https://api.openai.com/v1"}),ct);
        if(!r.IsSuccessStatusCode)throw new AuthError("授权续期未完成，请重新连接ChatGPT；不会自动重复尝试。");
        using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));var root=doc.RootElement;
        string V(string k)=>root.TryGetProperty(k,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString()??"":"";
        if(!root.TryGetProperty("expires_in",out var expires)||!expires.TryGetInt32(out var seconds)||seconds<=0||seconds>604800 || V("access_token").Length==0 || V("refresh_token").Length==0 || V("id_token").Length==0)throw new AuthError("续期字段不完整，请重新连接。");
        var next=new Credentials{ClientId=old.ClientId,Subject=old.Subject,Email=old.Email,AccessToken=V("access_token"),RefreshToken=V("refresh_token"),IdToken=V("id_token"),Scopes=V("scope"),ExpiresAt=DateTime.UtcNow.AddSeconds(seconds)};
        old.PendingRotation=next;store.Save(old); // 旋转后先加密保存检查点，验证失败也不丢失替代令牌。
        using var verificationTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var identity=await IdentityVerifier.VerifyOnline(next.IdToken,next.ClientId,null,verificationTimeout.Token);
        if(identity.Subject!=old.Subject || !OAuthProtocol.HasUsage(next.Scopes))throw new AuthError("续期身份或额度授权不匹配，请重新连接。");
        next.Email=identity.Email;store.Save(next);Session=next;ct.ThrowIfCancellationRequested();return next;
    }
    public async Task Disconnect(CancellationToken ct)
    {
        bool revoked=true;
        if(Session?.RefreshToken.Length>0)
        {
            try{using var response=await Http.PostAsync("https://auth.openai.com/api/accounts/oauth/revoke",new FormUrlEncodedContent(new Dictionary<string,string>{["token"]=Session.RefreshToken,["token_type_hint"]="refresh_token",["client_id"]=Session.ClientId}),ct);revoked=response.IsSuccessStatusCode;}catch{revoked=false;}
        }
        Forget();if(!revoked)throw new AuthError("已退出本机，但未确认服务端撤销，请到ChatGPT设置中断开应用。");
    }
    public void Dispose() { }
}
