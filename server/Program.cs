using System.Net;
using System.Net.Sockets;

const int Port = 7777;

TcpListener listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"HighPing 서버 시작 : 포트 {Port}");

uint nextId = 1;
while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();
    Session session = new Session(nextId, client);
    nextId++;
    _ = session.RunAsync();
}
