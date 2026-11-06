using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
namespace LiveAdConnect;
static class Checks
{
    public static void Run(string output)
    {
        int count=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;}
        Check(OAuthProtocol.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")=="E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM","RFC7636 PKCE");
        Check(OAuthProtocol.ParseCallback("GET /auth/callback?code=test&state=abc&client_id=oaiapp_test HTTP/1.1","abc",null).ClientId=="oaiapp_test","动态client");
        void Reject(string request,string state,string? client=null){try{OAuthProtocol.ParseCallback(request,state,client);}catch(AuthError){count++;return;}throw new Exception("应拒绝不匹配的回调");}
        Reject("GET /auth/callback?code=x&state=wrong&client_id=oaiapp_a HTTP/1.1","expected");
        Reject("GET /auth/callback?code=x&state=s HTTP/1.1","s");
        Reject("GET /auth/callback?code=x&state=s&client_id=oaiapp_b HTTP/1.1","s","oaiapp_a");
        Reject("GET /else?code=x&state=s&client_id=oaiapp_a HTTP/1.1","s");
        Reject("GET /auth/callback?code=x&state=s&state=s&client_id=oaiapp_a HTTP/1.1","s");
        Reject("GET /auth/callback?error=access_denied&state=s HTTP/1.1","s");
        Check(!OAuthProtocol.HasUsage("openid email"),"仅身份不授权额度");Check(OAuthProtocol.HasUsage("openid chatgpt.tokens.use.direct"),"scope确认");
        using var rsa=RSA.Create(2048);var key=new RsaSecurityKey(rsa){KeyId="test"};
        var issuer="https://auth.openai.com";var now=DateTime.UtcNow;
        string Token(string aud,string nonce)=>new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(issuer,aud,[new("sub","test-sub"),new("nonce",nonce)],now.AddMinutes(-1),now.AddMinutes(5),new SigningCredentials(key,SecurityAlgorithms.RsaSha256)));
        Check(IdentityVerifier.Verify(Token("oaiapp_test","n"),"oaiapp_test","n",[key]).Subject=="test-sub","签名身份");
        try{IdentityVerifier.Verify(Token("wrong","n"),"oaiapp_test","n",[key]);throw new Exception("aud未拒绝");}catch(AuthError){count++;}
        try{IdentityVerifier.Verify(Token("oaiapp_test","bad"),"oaiapp_test","n",[key]);throw new Exception("nonce未拒绝");}catch(AuthError){count++;}
        var sse="data: {\"type\":\"response.output_text.delta\",\"delta\":\"正常\"}\n\ndata: {\"type\":\"response.completed\"}\n\n";
        Check(Responses.ReadStream(new StringReader(sse),null,CancellationToken.None).GetAwaiter().GetResult()=="正常","SSE完成");
        try{Responses.ReadStream(new StringReader("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"),null,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("中断未拒绝");}catch(AuthError){count++;}
        var path=Path.Combine(Path.GetTempPath(),"LiveAdConnect-check-"+Guid.NewGuid());
        try{var storage=new CredentialStore(path);storage.Save(new(){ClientId="oaiapp_test",AccessToken="TEST-SECRET-NOT-REAL",Subject="test-sub",ExpiresAt=now.AddHours(1)});Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(path,"credentials.bin"))).Contains("TEST-SECRET"),"凭据不明文");Check(storage.Load()!.AccessToken=="TEST-SECRET-NOT-REAL","DPAPI回读");}
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
        File.WriteAllText(output,$"PASS {count} checks: PKCE, callback state/path/client/errors, scopes, JWT signature/audience/nonce, SSE completion, DPAPI storage.");
    }
}
