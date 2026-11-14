using System.Net.Sockets;
using System.Threading.Channels;

// 접속 1개 = Session 1개.
// 받기: 패킷을 읽어 GameWorld.Post 로 넘기기만 한다 (게임 상태는 직접 안 만짐)
// 보내기: Send()는 보내기 줄에 넣기만 하고, SendLoopAsync 하나가 순서대로 보낸다 (동시에 쓰면 바이트가 섞이므로)
class Session
{
    private const int MaxBody = 4096;
    private const int MaxSendQueue = 60;   // State 30Hz 기준 약 2초치. 넘으면 안 읽는 클라로 보고 끊음 (GH 쓰기 큐 상한)

    public uint Id { get; }
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly GameWorld _world;
    private readonly Channel<byte[]> _sendQueue;
    private volatile bool _closed;   // 게임 루프 스레드가 Send 에서 읽고, 수신 쪽이 Close 에서 씀

    public Session(uint id, TcpClient client, GameWorld world)
    {
        Id = id;
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _world = world;
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxSendQueue));
        _closed = false;
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"[접속] player {Id}");
        Task sendTask = SendLoopAsync();
        try
        {
            _world.Post(() => _world.OnJoin(this));

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
                HandlePacket(new PacketBuffer(body));
            }
        }
        catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException)
        {
            Console.WriteLine($"[퇴장] player {Id} : {e.Message}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[세션 오류] player {Id} : {e}");
        }
        finally
        {
            _world.Post(() => _world.OnLeave(this));
            Close();
        }
        await sendTask;
    }

    // 패킷 읽기는 여기(수신 쪽)에서 끝낸다: 모자란 패킷이면 예외가 이 세션에서만 나고 게임 루프는 안전
    private void HandlePacket(PacketBuffer packet)
    {
        PacketId id = packet.ReadId();
        switch (id)
        {
            case PacketId.Input:
                byte keyBits = packet.ReadU8();
                _world.Post(() => _world.OnInput(this, keyBits));
                break;
            case PacketId.Shoot:
                float aimX = packet.ReadF32();
                float aimZ = packet.ReadF32();
                if (!float.IsFinite(aimX) || !float.IsFinite(aimZ))
                {
                    break;   // 숫자가 아닌 값(NaN, 무한대)은 버림: 판정 계산에 들어가면 결과가 전부 망가짐
                }
                _world.Post(() => _world.OnShoot(this, aimX, aimZ));
                break;
            default:
                Console.WriteLine($"[무시] player {Id} : {id}");
                break;
        }
    }

    // 어느 스레드에서 불러도 됨: 보내기 줄에 넣기만 한다
    public void Send(PacketBuffer packet)
    {
        if (_closed)
        {
            return;
        }

        byte[] body = packet.Data();
        byte[] frame = new byte[2 + body.Length];
        frame[0] = (byte)(body.Length >> 8);
        frame[1] = (byte)(body.Length & 0xFF);
        body.CopyTo(frame, 2);

        // 실패 이유는 둘: 줄이 가득 참 / 방금 닫혀서 입구가 막힘(정상 퇴장). 가득 찬 경우만 끊는다
        if (!_sendQueue.Writer.TryWrite(frame) && !_closed)
        {
            Console.WriteLine($"[끊음] player {Id} : 보내기 줄이 {MaxSendQueue}개 가득 참");
            Close();
        }
    }

    // 보내기는 이 반복 하나만 한다
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (byte[] frame in _sendQueue.Reader.ReadAllAsync())
            {
                await _stream.WriteAsync(frame);
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException)
        {
            // 연결이 닫혀서 못 보냄 = 퇴장 처리는 RunAsync 쪽에서 함
        }
    }

    private void Close()
    {
        _closed = true;
        _sendQueue.Writer.TryComplete();
        _client.Close();
    }
}
