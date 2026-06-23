$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$uri = [Uri]"ws://127.0.0.1:12345/ws"
$ws.ConnectAsync($uri, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

$buffer = New-Object byte[] 8192
while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
    $ms = New-Object System.IO.MemoryStream
    do {
        $segment = [ArraySegment[byte]]::new($buffer)
        $result = $ws.ReceiveAsync($segment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($result.Count -gt 0) {
            $ms.Write($buffer, 0, $result.Count)
        }
    } while (-not $result.EndOfMessage)

    if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
        break
    }

    $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
    $text
}
