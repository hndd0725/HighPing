using System.Net.Sockets;

// 접속 1개 = Session 1개. 받은 패킷을 꺼내기만 한다 (게임 상태는 2단계 게임 루프가 만짐)
class Session
{
    private const int MaxBody = 4096;

    public uint Id { get; }
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public Session(uint id, TcpClient client)
    {
        Id = id;
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"[접속] player {Id}");
        try
        {
            PacketBuffer welcome = new PacketBuffer(PacketId.Welcome);
            welcome.WriteU32(Id);
            await SendAsync(welcome);

            byte[] header = new byte[2];
            while (true)
            {
                // [2B 길이] 를 먼저 다 받고, 그 길이만큼 body 를 다 받는다
                await _stream.ReadExactlyAsync(header);
                int len = (header[0] << 8) | header[1];
                if (len < 2 || len > MaxBody)
                {
                    throw new InvalidDataException($"잘못된 길이 {len}");
                }

                byte[] body = new byte[len];
                await _stream.ReadExactlyAsync(body);

                PacketBuffer packet = new PacketBuffer(body);
                PacketId id = packet.ReadId();
                Console.WriteLine($"[수신] player {Id} : {id} ({len}바이트)");
            }
        }
        catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException)
        {
            Console.WriteLine($"[퇴장] player {Id} : {e.Message}");
        }
        finally
        {
            _client.Close();
        }
    }

    public async Task SendAsync(PacketBuffer packet)
    {
        byte[] body = packet.Data();
        byte[] frame = new byte[2 + body.Length];
        frame[0] = (byte)(body.Length >> 8);
        frame[1] = (byte)(body.Length & 0xFF);
        body.CopyTo(frame, 2);
        await _stream.WriteAsync(frame);
    }
}
