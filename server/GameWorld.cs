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
    private const double ShootCooldownMs = 500;  // 연사 간격: 한 번 쏘면 0.5초 동안 못 쏨
    private const int PingOnHit = 0;             // 맞힌 쪽 핑 변화: 없음
    private const int PingOnHurt = 200;          // 맞은 쪽 핑 변화: +200 (맞을수록 잡기 어려운 유령이 됨)
    private const int PingMax = 3000;
    private const double RespawnDelayMs = 3000;  // 죽으면 3초 뒤 다시 태어남
    private const double InvincibleMs = 2000;    // 다시 태어난 뒤 2초 무적
    private const int KillsToWin = 3;            // 먼저 3번 죽이면 승리
    private const int MaxPlayers = 2;
    private const float JumpHeight = 1.8f;       // 점프 높이: 몸 높이 1.8m 의 3분의 2
    private const double JumpMs = 500;           // 떠 있는 시간
    private const double JumpRestMs = 300;       // 착지 후 다시 뛰기까지 쉬는 시간
    private const byte KeyJump = 16;             // 스페이스
    private const float MinSpawnGap = 15f;       // 입장·새 판 때 다른 사람과 이만큼은 떨어져 태어남
    private const int PingMin = 100;             // 아무리 맞아도 유령은 남게: 100ms x 8m/초 = 0.8m > 반지름 0.5m

    private readonly ConcurrentQueue<Action> _jobs;
    private readonly Dictionary<uint, Player> _players;
    private readonly Stopwatch _clock;
    private readonly Random _random;
    private readonly List<PendingShot> _pendingShots;   // 아직 안 보낸 Shot 패킷 (상대에게는 핑만큼 늦게 보냄)

    // 나중에 보낼 Shot 한 건
    private struct PendingShot
    {
        public double SendAtMs;
        public Session To;
        public PacketBuffer Packet;
    }
    private long _tickCount;

    public GameWorld()
    {
        _jobs = new ConcurrentQueue<Action>();
        _players = new Dictionary<uint, Player>();
        _clock = new Stopwatch();
        _random = new Random();
        _pendingShots = new List<PendingShot>();
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

        double nowMs = _clock.Elapsed.TotalMilliseconds;

        // 보낼 시각이 된 Shot 을 보낸다 (뒤에서부터 돌아야 지우면서 돌 수 있다)
        for (int i = _pendingShots.Count - 1; i >= 0; i--)
        {
            if (nowMs >= _pendingShots[i].SendAtMs)
            {
                _pendingShots[i].To.Send(_pendingShots[i].Packet);
                _pendingShots.RemoveAt(i);
            }
        }

        float dt = (float)(TickMs / 1000.0);
        foreach (Player player in _players.Values)
        {
            if (player.Dead)
            {
                // 죽은 사람은 못 움직인다. 시간이 되면 맵 안의 무작위 자리에 다시 태어난다
                if (nowMs >= player.RespawnAtMs)
                {
                    RandomSpot(out float x, out float z);
                    player.Spawn(x, z, nowMs + InvincibleMs);
                    Console.WriteLine($"[다시 태어남] player {player.Id} ({x:F1}, {z:F1}), {InvincibleMs / 1000}초 무적");
                }
                continue;
            }
            Move(player, dt);
            UpdateJump(player, nowMs);
        }

        // 핑 지연용: 이번 틱의 위치를 기록
        foreach (Player player in _players.Values)
        {
            player.Record(nowMs);
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
        if (_players.Count >= MaxPlayers)
        {
            Console.WriteLine($"[입장 거절] player {session.Id} : 이미 {MaxPlayers}명");
            session.Disconnect();
            return;
        }

        RandomSpotApart(new List<Player>(_players.Values), out float startX, out float startZ);
        _players[session.Id] = new Player(session, startX, startZ);

        PacketBuffer welcome = new PacketBuffer(PacketId.Welcome);
        welcome.WriteU32(session.Id);
        session.Send(welcome);
        Console.WriteLine($"[입장] player {session.Id} ({startX:F1}, {startZ:F1}), 현재 {_players.Count}명");
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
        if (!_players.TryGetValue(session.Id, out Player? player))
        {
            return;
        }

        // 스페이스를 "막 누른 순간"인가: 이번 키 상태엔 있고, 직전 키 상태엔 없었다
        // (누르고 있는 동안 계속 뛰지 않게. 다시 뛰려면 뗐다가 다시 눌러야 함)
        bool jumpPressed = (keyBits & KeyJump) != 0 && (player.KeyBits & KeyJump) == 0;
        player.KeyBits = keyBits;

        double nowMs = _clock.Elapsed.TotalMilliseconds;
        bool onGround = player.JumpStartMs < 0;
        if (jumpPressed && !player.Dead && onGround && nowMs >= player.NextJumpMs)
        {
            player.JumpStartMs = nowMs;
        }
    }

    // 사격: 쏜 사람의 실제 위치에서 조준점 방향으로 맵 끝까지 나가는 직선이, 상대의 "실제 위치"에 닿는지 본다
    // (상대 화면의 유령이 아니라 실제 위치로 판정한다 = 고핑의 규칙. 랙 보상을 일부러 안 함)
    public void OnShoot(Session session, float aimX, float aimZ)
    {
        if (!_players.TryGetValue(session.Id, out Player? shooter) || shooter.Dead)
        {
            return;   // 죽어 있는 동안은 못 쏜다
        }

        // 방향 = 조준점 - 내 위치. 길이를 1 로 만든다 (방향만 남기려고)
        float dirX = aimX - shooter.X;
        float dirZ = aimZ - shooter.Z;
        float length = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
        if (length < 0.001f)
        {
            return;   // 자기 발밑을 찍음: 방향이 없다 (쏜 것으로 치지 않음)
        }
        dirX /= length;
        dirZ /= length;

        // 연사 간격: 아직 못 쏘는 시각이면 이 사격은 버린다 (몰아 보내도 0.5초에 1발만 처리)
        double nowMs = _clock.Elapsed.TotalMilliseconds;
        if (nowMs < shooter.NextShootMs)
        {
            return;
        }
        shooter.NextShootMs = nowMs + ShootCooldownMs;

        // Shot 에 넣을 값은 지금 적어 둔다 (아래에서 승리 처리가 되면 자리와 핑이 바뀌므로)
        float fromX = shooter.X;
        float fromZ = shooter.Z;
        double shownDelayMs = VisibleDelayMs(shooter, nowMs);
        float reach = DistanceToMapEdge(fromX, fromZ, dirX, dirZ);   // 총알이 가는 거리: 안 맞으면 맵 끝까지
        bool hit = false;

        foreach (Player target in _players.Values)
        {
            if (target.Id == shooter.Id || target.Dead)
            {
                continue;   // 죽어 있는 상대는 맞지 않는다
            }
            if (target.JumpStartMs >= 0)
            {
                continue;   // 점프 중인 상대는 맞지 않는다 (총알이 발밑으로 지나감)
            }
            if (RayHitsCircle(fromX, fromZ, dirX, dirZ, target.X, target.Z, Radius))
            {
                // 총알은 상대 앞에서 멈춘다 (무적이어도 몸에 막힘)
                reach = Math.Max((target.X - fromX) * dirX + (target.Z - fromZ) * dirZ, 0f);

                if (nowMs < target.InvincibleUntilMs)
                {
                    Console.WriteLine($"[무적] player {shooter.Id} -> player {target.Id} : 다시 태어난 직후라 안 다침");
                    break;
                }

                // 맞은 사람은 핑이 오르고(더 잡기 어려운 유령이 됨) HP 가 깎인다. 맞힌 사람은 변화 없음
                hit = true;
                shooter.PingMs = (ushort)Math.Clamp(shooter.PingMs + PingOnHit, PingMin, PingMax);
                target.PingMs = (ushort)Math.Clamp(target.PingMs + PingOnHurt, PingMin, PingMax);
                target.Hp--;
                Console.WriteLine($"[명중] player {shooter.Id} -> player {target.Id} : 핑 {shooter.PingMs} / {target.PingMs}, 맞은 쪽 HP {target.Hp}");

                if (target.Hp == 0)
                {
                    Kill(shooter, target, nowMs);
                }
                break;
            }
        }
        if (!hit)
        {
            Console.WriteLine($"[빗나감] player {shooter.Id}");
        }

        // 총알이 날아간 선을 알린다. 쏜 사람에게는 바로, 다른 사람에게는 "쏜 사람이 보이는 지연"만큼 늦게
        // (바로 보내면 쏜 사람의 실제 위치가 상대 컴퓨터에 먼저 도착한다. 늦게 보내면 그때 유령이 딱 그 자리에 있다)
        PacketBuffer shot = new PacketBuffer(PacketId.Shot);
        shot.WriteU32(shooter.Id);
        shot.WriteF32(fromX);
        shot.WriteF32(fromZ);
        shot.WriteF32(fromX + dirX * reach);
        shot.WriteF32(fromZ + dirZ * reach);
        shot.WriteU8((byte)(hit ? 1 : 0));
        foreach (Player player in _players.Values)
        {
            if (player.Id == shooter.Id)
            {
                player.Session.Send(shot);
            }
            else
            {
                PendingShot pending = new PendingShot();
                pending.SendAtMs = nowMs + shownDelayMs;
                pending.To = player.Session;
                pending.Packet = shot;
                _pendingShots.Add(pending);
            }
        }
    }

    // 이 플레이어가 상대에게 얼마나 과거로 보이나: 보통은 핑만큼, 무적인 동안은 0 (실제 위치가 그대로 보임)
    private static double VisibleDelayMs(Player player, double nowMs)
    {
        if (nowMs < player.InvincibleUntilMs)
        {
            return 0;
        }
        return player.PingMs;
    }

    // (x, z) 에서 (dirX, dirZ) 방향으로 가면 맵 가장자리까지 얼마나 남았나
    private static float DistanceToMapEdge(float x, float z, float dirX, float dirZ)
    {
        float distance = float.MaxValue;
        if (dirX > 0)
        {
            distance = Math.Min(distance, (HalfWidth - x) / dirX);
        }
        else if (dirX < 0)
        {
            distance = Math.Min(distance, (-HalfWidth - x) / dirX);
        }
        if (dirZ > 0)
        {
            distance = Math.Min(distance, (HalfHeight - z) / dirZ);
        }
        else if (dirZ < 0)
        {
            distance = Math.Min(distance, (-HalfHeight - z) / dirZ);
        }
        return distance;
    }

    // 맵 안의 무작위 자리 (벽에 끼지 않게 반지름만큼 안쪽)
    private void RandomSpot(out float x, out float z)
    {
        x = (_random.NextSingle() * 2 - 1) * (HalfWidth - Radius);
        z = (_random.NextSingle() * 2 - 1) * (HalfHeight - Radius);
    }

    // 무작위 자리를 고르되, others 의 모든 사람과 MinSpawnGap 이상 떨어진 곳 (30번 안에 못 찾으면 마지막 자리)
    private void RandomSpotApart(List<Player> others, out float x, out float z)
    {
        x = 0;
        z = 0;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            RandomSpot(out x, out z);
            bool farEnough = true;
            foreach (Player other in others)
            {
                float dx = other.X - x;
                float dz = other.Z - z;
                if (dx * dx + dz * dz < MinSpawnGap * MinSpawnGap)
                {
                    farEnough = false;
                }
            }
            if (farEnough)
            {
                return;
            }
        }
    }

    // 상대의 HP 가 0 이 됨: 죽은 사람은 3초 뒤 다시 태어나고, 죽인 사람은 1킬. 3킬이면 승리
    private void Kill(Player killer, Player victim, double nowMs)
    {
        victim.Dead = true;
        victim.RespawnAtMs = nowMs + RespawnDelayMs;
        killer.Kills++;
        Console.WriteLine($"[사망] player {victim.Id} (죽인 쪽 player {killer.Id}, {killer.Kills}킬)");

        if (killer.Kills >= KillsToWin)
        {
            Console.WriteLine($"[승리] player {killer.Id}");
            PacketBuffer roundEnd = new PacketBuffer(PacketId.RoundEnd);
            roundEnd.WriteU32(killer.Id);
            foreach (Player player in _players.Values)
            {
                player.Session.Send(roundEnd);
            }

            // 새 판: 모두 킬 수 0, 서로 떨어진 무작위 자리에서 다시 (무적 없음)
            List<Player> placed = new List<Player>();
            foreach (Player player in _players.Values)
            {
                player.Kills = 0;
                RandomSpotApart(placed, out float x, out float z);
                player.Spawn(x, z, 0);
                placed.Add(player);
            }
        }
    }

    // [★ 본인 빈칸 ④] (fromX, fromZ) 에서 (dirX, dirZ) 방향으로 나가는 직선이, 중심 (centerX, centerZ) 반지름 radius 인 원에 닿는가
    // (dirX, dirZ) 는 길이가 1 인 방향이다
    private static bool RayHitsCircle(float fromX, float fromZ, float dirX, float dirZ, float centerX, float centerZ, float radius)
    {
        // v = 쏜 곳에서 상대 중심까지 가는 화살표
        float vx = centerX - fromX;
        float vz = centerZ - fromZ;

        // 쏜 곳이 이미 상대 원 안이면(둘이 겹쳐 있음) 어느 방향으로 쏴도 맞는다
        // (이 줄이 없으면 겹친 채로 상대 중심 반대쪽을 쏠 때 "뒤쪽"으로 처리되어 빗나감 - 실험으로 확인)
        if (vx * vx + vz * vz < radius * radius)
        {
            return true;
        }

        // 빈칸 1: 상대가 "쏜 방향으로 얼마나 앞에" 있나
        float forward = vx * dirX + vz * dirZ;

        // 빈칸 2: 뒤쪽에 있으면 안 맞는다
        if (forward<0)
        {
            return false;
        }

        // 빈칸 3: 상대가 직선에서 "옆으로 얼마나 떨어져" 있나 (의 제곱)
        float sideSquared = vx * vx + vz * vz - forward * forward;

        // 빈칸 4: 옆으로 떨어진 거리가 반지름 이하이면 맞음
        return sideSquared < radius * radius;
    }

    // ------------------------------ 틱 안에서 하는 일 ------------------------------
    // 점프 중이면 높이를 계산한다. 떠 있는 시간이 다 지나면 착지하고, 쉬는 시간을 적어 둔다
    // 높이 = 4 x 최고 높이 x t x (1 - t),  t = 뛴 뒤 흐른 비율 (0 ~ 1)
    //   t = 0 -> 0,   t = 0.25 -> 0.9m,   t = 0.5 -> 1.2m (최고),   t = 0.75 -> 0.9m,   t = 1 -> 0
    private static void UpdateJump(Player player, double nowMs)
    {
        if (player.JumpStartMs < 0)
        {
            return;
        }

        double t = (nowMs - player.JumpStartMs) / JumpMs;
        if (t >= 1.0)
        {
            player.Y = 0;
            player.JumpStartMs = -1;
            player.NextJumpMs = nowMs + JumpRestMs;
            return;
        }
        player.Y = (float)(4 * JumpHeight * t * (1 - t));
    }

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

    // 보는 사람마다 State 를 만든다: 맨 앞에 "내 실제 위치", 목록은 전부 유령 위치 (구조 B안)
    // 유령 위치 = 그 플레이어의 (지금 - 그 플레이어의 핑) 시각 위치. 내 유령도 목록에 들어 있다
    private void BroadcastState()
    {
        double nowMs = _clock.Elapsed.TotalMilliseconds;
        foreach (Player viewer in _players.Values)
        {
            PacketBuffer state = new PacketBuffer(PacketId.State);
            state.WriteU32((uint)nowMs);
            state.WriteF32(viewer.X);
            state.WriteF32(viewer.Y);
            state.WriteF32(viewer.Z);
            state.WriteU16((ushort)_players.Count);
            foreach (Player player in _players.Values)
            {
                player.FindPast(nowMs - VisibleDelayMs(player, nowMs), out float ghostX, out float ghostY, out float ghostZ);
                state.WriteU32(player.Id);
                state.WriteF32(ghostX);
                state.WriteF32(ghostY);
                state.WriteF32(ghostZ);
                state.WriteU16(player.PingMs);
                state.WriteU8(player.Hp);

                // 상태 비트: 1 = 죽어 있음, 2 = 무적
                byte flags = 0;
                if (player.Dead)
                {
                    flags |= 1;
                }
                if (nowMs < player.InvincibleUntilMs)
                {
                    flags |= 2;
                }
                state.WriteU8(flags);
                state.WriteU8(player.Kills);
            }
            viewer.Session.Send(state);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);
}
