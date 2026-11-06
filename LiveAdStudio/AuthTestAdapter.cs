namespace LiveAdConnect;
static class Responses
{
    public static async Task<string> ReadStream(TextReader r,Action<string>? delta,CancellationToken ct)=>(await LiveAdStudio.AiClient.Read(r,delta,ct)).Text;
}
