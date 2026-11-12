using System.Net;
using System.Net.Sockets;

const int Port = 7777;

GameWorld world = new GameWorld();
world.Start();

TcpListener listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"HighPing 서버 시작 : 포트 {Port}");

uint nextId = 1;
while (true)
{
    // 접속하자마자 강제로 끊는 연결 하나 때문에 서버 전체가 죽지 않게 (RST 300번 실험에서 재현됨)
    try
    {
        TcpClient client = await listener.AcceptTcpClientAsync();
        Session session = new Session(nextId, client, world);
        nextId++;
        _ = session.RunAsync();
    }
    catch (Exception e) when (e is SocketException || e is IOException || e is InvalidOperationException)
    {
        Console.WriteLine($"[접속 실패] {e.Message}");
    }
}
