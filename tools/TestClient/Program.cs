using System.Net.Sockets;

// 서버 1단계 확인용: 접속 → Welcome 받기 → Input 2개 보내기 → 종료
TcpClient client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 7777);
NetworkStream stream = client.GetStream();

// Welcome 받기: [2B 길이] → body
byte[] header = new byte[2];
await stream.ReadExactlyAsync(header);
int len = (header[0] << 8) | header[1];
byte[] body = new byte[len];
await stream.ReadExactlyAsync(body);

PacketBuffer welcome = new PacketBuffer(body);
Console.WriteLine($"받음 : {welcome.ReadId()} myId={welcome.ReadU32()}");

// Input 보내기: W 누름(1) → 뗌(0)
foreach (byte keyBits in new byte[] { 1, 0 })
{
    PacketBuffer input = new PacketBuffer(PacketId.Input);
    input.WriteU8(keyBits);
    byte[] data = input.Data();
    byte[] frame = new byte[2 + data.Length];
    frame[0] = (byte)(data.Length >> 8);
    frame[1] = (byte)(data.Length & 0xFF);
    data.CopyTo(frame, 2);
    await stream.WriteAsync(frame);
    Console.WriteLine($"보냄 : Input keyBits={keyBits} ({BitConverter.ToString(frame)})");
}

await Task.Delay(200);
client.Close();
