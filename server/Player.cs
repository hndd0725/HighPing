// 위치 기록 한 줄: 어느 시각에 어디 있었나
struct PosRecord
{
    public double TimeMs;
    public float X;
    public float Y;   // 점프 높이 (바닥 = 0)
    public float Z;
}

// 플레이어 1명의 게임 상태. GameWorld 루프 스레드만 읽고 쓴다
class Player
{
    public uint Id { get; }
    public Session Session { get; }
    public float X;
    public float Z;
    public byte KeyBits;   // 지금 누르고 있는 키 (W=1, A=2, S=4, D=8, 스페이스=16)
    public float Y;              // 점프 높이 (바닥 = 0)
    public double JumpStartMs;   // 점프를 시작한 시각. -1 이면 땅에 있음
    public double NextJumpMs;    // 이 시각 전에는 다시 못 뛴다 (착지 후 쉬는 시간)
    public ushort PingMs;
    public byte Hp;
    public double NextShootMs;   // 이 시각 전에는 다시 못 쏜다 (연사 간격)
    public byte Kills;           // 이번 판에서 상대를 죽인 횟수
    public bool Dead;            // 죽어서 다시 태어나기를 기다리는 중
    public double RespawnAtMs;   // 다시 태어날 시각 (Dead 일 때만 의미 있음)
    public double InvincibleUntilMs;   // 이 시각 전에는 맞아도 안 다친다 (다시 태어난 직후)
    public List<PosRecord> History { get; }   // 틱마다 쌓는 위치 기록. 앞이 오래된 것, 뒤가 최신

    private const double HistoryKeepMs = 3500;   // 핑 최대 3000ms + 여유

    public Player(Session session, float x, float z)
    {
        Id = session.Id;
        Session = session;
        X = x;
        Z = z;
        KeyBits = 0;
        PingMs = 200;
        Hp = 3;
        NextShootMs = 0;
        Kills = 0;
        Dead = false;
        RespawnAtMs = 0;
        InvincibleUntilMs = 0;
        Y = 0;
        JumpStartMs = -1;
        NextJumpMs = 0;
        History = new List<PosRecord>();
    }

    // (다시) 태어난다: 자리를 옮기고 HP·핑을 처음 값으로. invincibleUntilMs 전까지는 무적
    // 위치 기록을 비운다: 안 비우면 유령이 죽은 자리에서 새 자리까지 미끄러져 가는 것이 핑 시간만큼 보인다
    public void Spawn(float x, float z, double invincibleUntilMs)
    {
        X = x;
        Z = z;
        Hp = 3;
        PingMs = 200;
        Dead = false;
        InvincibleUntilMs = invincibleUntilMs;
        Y = 0;
        JumpStartMs = -1;
        History.Clear();
    }

    // 틱마다 부른다: 지금 위치를 기록 끝에 붙이고, 너무 오래된 것은 앞에서 버린다
    public void Record(double nowMs)
    {
        PosRecord record = new PosRecord();
        record.TimeMs = nowMs;
        record.X = X;
        record.Y = Y;
        record.Z = Z;
        History.Add(record);

        while (History[0].TimeMs < nowMs - HistoryKeepMs)
        {
            History.RemoveAt(0);
        }
    }

    // [★ 본인 빈칸 ③] 기록에서 targetMs 시각에 있던 위치를 찾아 x, z 에 넣는다
    //
    // 기록 예 (60Hz 라 약 16.7ms 간격):   [0] 1000.0ms (3.00, 0)   [1] 1016.7ms (3.13, 0)   [2] 1033.3ms (3.27, 0)
    // targetMs = 1025.0 이면 [1] 과 [2] 사이 -> 둘을 비율로 섞는다
    //   비율 = (1025.0 - 1016.7) / (1033.3 - 1016.7) = 0.5      x = 3.13 + (3.27 - 3.13) * 0.5 = 3.20
    //
    // 순서:
    //   1) targetMs 가 가장 오래된 기록(History[0])보다 이전이면 -> History[0] 의 위치
    //   2) 뒤에서부터 보면서 TimeMs 가 targetMs 이하인 첫 기록 a 를 찾는다. 그 다음 기록이 b
    //   3) a 가 마지막 기록이면(b 가 없으면) -> a 의 위치
    //   4) 비율을 구해 a 와 b 사이를 섞는다
    // (11/16 점프 추가: 높이 y 도 x, z 와 똑같이 섞는다 -> 상대에게는 점프도 핑만큼 늦게 보임)
    public void FindPast(double targetMs, out float x, out float y, out float z)
    {
        // 뒤(최신)에서부터 앞으로 가면서, 시각이 targetMs 이하인 첫 줄을 찾는다
        for (int i = History.Count - 1; i >= 0; i--)
        {
            if (History[i].TimeMs <= targetMs)
            {
                PosRecord a = History[i];

                // a 가 마지막 줄이면 다음 줄(b)이 없다 -> a 의 위치를 그대로
                if (i == History.Count - 1)
                {
                    x = a.X;
                    y = a.Y;
                    z = a.Z;
                    return;
                }

                PosRecord b = History[i + 1];

                // a 에서 b 까지 가는 길에 얼마나 왔나 (0 이면 a, 1 이면 b)
                double ratio = (targetMs - a.TimeMs) / (b.TimeMs - a.TimeMs);

                x = (float)(a.X + (b.X - a.X) * ratio);
                y = (float)(a.Y + (b.Y - a.Y) * ratio);
                z = (float)(a.Z + (b.Z - a.Z) * ratio);
                return;
            }
        }

        // 여기까지 왔다 = targetMs 가 가장 오래된 줄보다도 이전 -> 가장 오래된 줄의 위치
        x = History[0].X;
        y = History[0].Y;
        z = History[0].Z;
    }
}
