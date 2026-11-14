using System.Net.Sockets;

// 서버 2단계 확인용: 접속 → 키 하나를 1초 누름 → 뗌 → 0.5초 뒤 종료
// 실행: dotnet run -- D   (W / A / S / D 중 하나, 없으면 D)
string key = (args.Length > 0) ? args[0] : "D";
byte keyBits = 0;
if (key == "W")
{
    keyBits = 1;
}
else if (key == "A")
{
    keyBits = 2;
}
else if (key == "S")
{
    keyBits = 4;
}
else if (key == "D")
{
    keyBits = 8;
}

TcpClient client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 7777);
NetworkStream stream = client.GetStream();

// Welcome 은 먼저 하나 받는다
PacketBuffer welcome = await ReceiveAsync(stream);
welcome.ReadId();
uint myId = welcome.ReadU32();
Console.WriteLine($"[{key}] Welcome myId={myId}");

// 받기는 따로 계속: State 15개(0.5초)마다 내 위치 출력
int stateCount = 0;
Task receiveTask = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            PacketBuffer packet = await ReceiveAsync(stream);
            if (packet.ReadId() != PacketId.State)
            {
                continue;
            }
            uint serverMs = packet.ReadU32();
            float myX = packet.ReadF32();
            float myZ = packet.ReadF32();
            ushort count = packet.ReadU16();
            stateCount++;
            for (int i = 0; i < count; ++i)
            {
                uint id = packet.ReadU32();
                float x = packet.ReadF32();
                float z = packet.ReadF32();
                ushort ping = packet.ReadU16();
                byte hp = packet.ReadU8();
                if (id == myId && stateCount % 15 == 0)
                {
                    double gap = Math.Sqrt((myX - x) * (myX - x) + (myZ - z) * (myZ - z));
                    Console.WriteLine($"[{key}] State#{stateCount} serverMs={serverMs} 인원={count} 실제=({myX:F2}, {myZ:F2}) 유령=({x:F2}, {z:F2}) 차이={gap:F2}m ping={ping} hp={hp}");
                }
            }
        }
    }
    catch (Exception e) when (e is EndOfStreamException || e is IOException || e is ObjectDisposedException)
    {
        // 연결 닫힘 = 종료
    }
});

await SendInputAsync(stream, keyBits);
await Task.Delay(1000);
await SendInputAsync(stream, 0);
await Task.Delay(500);
Console.WriteLine($"[{key}] 1.5초 동안 State {stateCount}개 받음 (30Hz면 약 45개)");
client.Close();
await receiveTask;

static async Task<PacketBuffer> ReceiveAsync(NetworkStream stream)
{
    byte[] header = new byte[2];
    await stream.ReadExactlyAsync(header);
    int len = (header[0] << 8) | header[1];
    byte[] body = new byte[len];
    await stream.ReadExactlyAsync(body);
    return new PacketBuffer(body);
}

static async Task SendInputAsync(NetworkStream stream, byte keyBits)
{
    PacketBuffer input = new PacketBuffer(PacketId.Input);
    input.WriteU8(keyBits);
    byte[] body = input.Data();
    byte[] frame = new byte[2 + body.Length];
    frame[0] = (byte)(body.Length >> 8);
    frame[1] = (byte)(body.Length & 0xFF);
    body.CopyTo(frame, 2);
    await stream.WriteAsync(frame);
}
