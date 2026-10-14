using System.Net;
using System.Text;

namespace InstagramFeed.Tests.Fakes;

/// <summary>Antwortet deterministisch und zaehlt die Aufrufe - damit ist belegbar, dass ein
/// vorhandenes Bild NICHT erneut geladen wird.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public int CallCount { get; private set; }

    public List<string> Requests { get; } = new();

    public static StubHttpMessageHandler Always(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(responder);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Image(string contentType = "image/jpeg", int bytes = 128) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } } };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request.RequestUri?.ToString() ?? "");
        return Task.FromResult(_responder(request));
    }
}
