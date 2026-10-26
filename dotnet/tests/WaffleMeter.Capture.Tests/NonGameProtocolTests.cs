using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

public class NonGameProtocolTests
{
    // 10-16 NAVER 스트리밍 커넥터(127.0.0.1:17080)로 가는 크롬 요청 모양.
    private const string ConnectorRequest =
        "GET /chzzk/live/seg_000123.ts HTTP/1.1\r\nHost: localhost:17080\r\nConnection: keep-alive\r\n" +
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/141.0.0.0 Safari/537.36\r\nAccept: */*\r\n\r\n";

    private static NonGameVerdict Classify(string text) => NonGameProtocol.Classify(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void A_complete_request_head_is_http() =>
        Assert.Equal(NonGameVerdict.HttpRequest, Classify(ConnectorRequest));

    [Theory]
    [InlineData("POST /api/v1/log HTTP/1.0\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("OPTIONS * HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("DELETE /x HTTP/1.1\r\nHost: a\r\n\r\n")]
    public void Every_listed_method_is_recognized(string head) =>
        Assert.Equal(NonGameVerdict.HttpRequest, Classify(head));

    [Fact]
    public void An_unfinished_head_is_left_undecided() =>
        Assert.Equal(NonGameVerdict.None, Classify("GET /chzzk/live/seg_000123.ts HTTP/1.1\r\nHost: localhost:17080\r\n"));

    [Theory]
    [InlineData("CONNECT game.example:13328 HTTP/1.1\r\nHost: game.example:13328\r\n\r\n")]
    [InlineData("GET /ws HTTP/1.1\r\nHost: a\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")]
    [InlineData("GET /ws HTTP/1.1\r\nHost: a\r\nupgrade: h2c\r\n\r\n")]
    [InlineData("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")]
    public void A_tunnel_opener_is_a_tunnel_not_http(string head) =>
        Assert.Equal(NonGameVerdict.Tunnel, Classify(head));

    [Theory]
    // CONNECT 프록시는 이 응답 뒤에 그 연결로 게임을 싣는다 — 응답 상태줄로는 절대 표시하지 않는다.
    [InlineData("HTTP/1.1 200 Connection established\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\n\r\n")]
    [InlineData("GET /x HTTP/2\r\nHost: a\r\n\r\n")]          // HTTP/1.x 아님
    [InlineData("GET  /x HTTP/1.1\r\nHost: a\r\n\r\n")]        // 빈 target
    [InlineData("GET /x HTTP/1.1\nHost: a\r\n\r\n")]          // CRLF 아님
    [InlineData("get /x HTTP/1.1\r\nHost: a\r\n\r\n")]        // 메서드는 대문자뿐
    public void Anything_short_of_a_request_head_is_not_marked(string text) =>
        Assert.Equal(NonGameVerdict.None, Classify(text));

    [Fact]
    public void Real_game_frames_are_never_marked()
    {
        byte[] mapFrame = [0x0E, 0x00, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00, 0x00, 0x08, 0x04, 0x38, 0x00, 0x00];
        byte[] joinRequest =
        [
            0x3e, 0x07, 0x97, 0x1e, 0x23, 0x02, 0x00, 0xaa, 0x72, 0x01, 0x00, 0x00, 0x00, 0xe9,
            0x03, 0x1a, 0x00, 0x00, 0x00, 0x2d, 0x00, 0x00, 0x00, 0xec, 0x0f, 0x00, 0x00, 0x09,
            0xec, 0xbf, 0xb5, 0xed, 0x95, 0xb4, 0xec, 0xab, 0x91, 0xe9, 0x03, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(NonGameVerdict.None, NonGameProtocol.Classify(mapFrame));
        Assert.Equal(NonGameVerdict.None, NonGameProtocol.Classify(joinRequest));
    }

    [Fact]
    public void A_request_line_followed_by_binary_is_not_marked()
    {
        // 요청줄처럼 시작해도 헤드가 같은 세그먼트에서 끝나지 않으면 판단을 보류한다 — 바이트가 우연히 요청줄
        // 모양을 만들 수는 있어도 \r\n\r\n 으로 닫힌 헤드까지 만들 수는 없다는 것이 이 분류의 근거다.
        var rng = new Random(20261027);
        byte[] line = Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\n");
        byte[] segment = new byte[line.Length + 1024];
        line.CopyTo(segment, 0);
        for (int i = 0; i < 1000; i++)
        {
            rng.NextBytes(segment.AsSpan(line.Length));
            for (int j = line.Length; j + 3 < segment.Length; j++)
            {
                if (segment[j] == '\r' && segment[j + 1] == '\n' && segment[j + 2] == '\r' && segment[j + 3] == '\n')
                {
                    segment[j] = 0;
                }
            }

            Assert.Equal(NonGameVerdict.None, NonGameProtocol.Classify(segment));
        }
    }
}
