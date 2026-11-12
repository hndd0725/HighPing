using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

// 게임 상태는 이 클래스의 루프 스레드 하나만 만진다 (B안: 큐 + 게임 루프)
// 다른 스레드는 Post()로 할 일을 큐에 넣기만 한다 (GH의 post(strand, 람다)와 같은 생각)
class GameWorld
{
    private const double TickMs = 1000.0 / 60;   // 60Hz
    private const int StateEveryTicks = 2;       // 60Hz / 2 = State 30Hz
    private const float Speed = 8f;              // m/초
    private const float HalfWidth = 20f;         // 맵 40 x 30 (가운데가 0, 0)
    private const float HalfHeight = 15f;
    private const float Radius = 0.5f;
    private const int ReportEveryMs = 5000;      // 틱 횟수 출력 간격
    private const double MaxCatchUpMs = 250;     // 이보다 많이 밀리면 따라잡지 않고 버림 (15틱)

    private readonly ConcurrentQueue<Action> _jobs;
    private readonly Dictionary<uint, Player> _players;
    private readonly Stopwatch _clock;
    private long _tickCount;

    public GameWorld()
    {
        _jobs = new ConcurrentQueue<Action>();
        _players = new Dictionary<uint, Player>();
        _clock = new Stopwatch();
        _tickCount = 0;
    }

    // 어느 스레드에서 불러도 됨: 할 일을 큐에 넣기만 한다
    public void Post(Action job)
    {
        _jobs.Enqueue(job);
    }

    public void Start()
    {
        Thread thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Start();
    }

    // 틱 방식 B: 다음 예정 시각을 지났으면 틱 (늦게 깼으면 밀린 만큼 연달아), 시계 눈금은 1ms
    private void Loop()
    {
        if (OperatingSystem.IsWindows())
        {
            timeBeginPeriod(1);
        }
        _clock.Start();

        double nextTickMs = 0;
        long lastReportMs = 0;
        long lastReportTick = 0;
        while (true)
        {
            // 루프가 오래 멈췄다 풀리면(콘솔 창 선택, 디버거 등) 밀린 틱을 다 돌리지 않고 버린다
            double lagMs = _clock.Elapsed.TotalMilliseconds - nextTickMs;
            if (lagMs > MaxCatchUpMs)
            {
                Console.WriteLine($"[틱] {lagMs:F0}ms 밀림 -> 밀린 틱을 버림");
                nextTickMs = _clock.Elapsed.TotalMilliseconds;
            }

            while (_clock.Elapsed.TotalMilliseconds >= nextTickMs)
            {
                Tick();
                nextTickMs += TickMs;
            }

            long nowMs = _clock.ElapsedMilliseconds;
            if (nowMs - lastReportMs >= ReportEveryMs)
            {
                Console.WriteLine($"[틱] {nowMs - lastReportMs}ms 동안 {_tickCount - lastReportTick}틱 (목표 {(nowMs - lastReportMs) * 60 / 1000})");
                lastReportMs = nowMs;
                lastReportTick = _tickCount;
            }

            Thread.Sleep(1);
        }
    }

    private void Tick()
    {
        while (_jobs.TryDequeue(out Action? job))
        {
            // 할 일 하나가 예외를 던져도 게임 루프(서버 전체)는 계속 돈다
            try
            {
                job();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[게임 루프 오류] {e}");
            }
        }

        float dt = (float)(TickMs / 1000.0);
        foreach (Player player in _players.Values)
        {
            Move(player, dt);
        }

        _tickCount++;
        if (_tickCount % StateEveryTicks == 0)
        {
            BroadcastState();
        }
    }

    // ------------------------------ Post 로 들어오는 일 ------------------------------
    public void OnJoin(Session session)
    {
        float startX = (session.Id % 2 == 1) ? -15f : 15f;   // 홀수 번호는 왼쪽, 짝수는 오른쪽
        _players[session.Id] = new Player(session, startX, 0f);

        PacketBuffer welcome = new PacketBuffer(PacketId.Welcome);
        welcome.WriteU32(session.Id);
        session.Send(welcome);
        Console.WriteLine($"[입장] player {session.Id} ({startX}, 0), 현재 {_players.Count}명");
    }

    public void OnLeave(Session session)
    {
        if (_players.Remove(session.Id))
        {
            Console.WriteLine($"[게임 퇴장] player {session.Id}, 현재 {_players.Count}명");
        }
    }

    public void OnInput(Session session, byte keyBits)
    {
        if (_players.TryGetValue(session.Id, out Player? player))
        {
            player.KeyBits = keyBits;
        }
    }

    // ------------------------------ 틱 안에서 하는 일 ------------------------------
    private static void Move(Player player, float dt)
    {
        float dx = 0;
        float dz = 0;
        if ((player.KeyBits & 1) != 0)   // W: 화면 위쪽 = -z
        {
            dz -= 1;
        }
        if ((player.KeyBits & 2) != 0)   // A
        {
            dx -= 1;
        }
        if ((player.KeyBits & 4) != 0)   // S
        {
            dz += 1;
        }
        if ((player.KeyBits & 8) != 0)   // D
        {
            dx += 1;
        }
        if (dx != 0 && dz != 0)
        {
            // 대각선도 같은 속도로: 1 / √2
            dx *= 0.7071f;
            dz *= 0.7071f;
        }

        player.X = Math.Clamp(player.X + dx * Speed * dt, -HalfWidth + Radius, HalfWidth - Radius);
        player.Z = Math.Clamp(player.Z + dz * Speed * dt, -HalfHeight + Radius, HalfHeight - Radius);
    }

    // 1주차 최소판: 모두에게 같은 State (보는 사람별 핑 지연은 11/12)
    private void BroadcastState()
    {
        PacketBuffer state = new PacketBuffer(PacketId.State);
        state.WriteU32((uint)_clock.ElapsedMilliseconds);
        state.WriteU16((ushort)_players.Count);
        foreach (Player player in _players.Values)
        {
            state.WriteU32(player.Id);
            state.WriteF32(player.X);
            state.WriteF32(player.Z);
            state.WriteU16(player.PingMs);
            state.WriteU8(player.Hp);
        }

        foreach (Player player in _players.Values)
        {
            player.Session.Send(state);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);
}
