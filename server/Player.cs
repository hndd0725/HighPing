// 위치 기록 한 줄: 어느 시각에 어디 있었나
struct PosRecord
{
    public double TimeMs;
    public float X;
    public float Z;
}

// 플레이어 1명의 게임 상태. GameWorld 루프 스레드만 읽고 쓴다
class Player
{
    public uint Id { get; }
    public Session Session { get; }
    public float X;
    public float Z;
    public byte KeyBits;   // 지금 누르고 있는 키 (W=1, A=2, S=4, D=8)
    public ushort PingMs;
    public byte Hp;
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
        History = new List<PosRecord>();
    }

    // 틱마다 부른다: 지금 위치를 기록 끝에 붙이고, 너무 오래된 것은 앞에서 버린다
    public void Record(double nowMs)
    {
        PosRecord record = new PosRecord();
        record.TimeMs = nowMs;
        record.X = X;
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
    public void FindPast(double targetMs, out float x, out float z)
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
                    z = a.Z;
                    return;
                }

                PosRecord b = History[i + 1];

                // a 에서 b 까지 가는 길에 얼마나 왔나 (0 이면 a, 1 이면 b)
                double ratio = (targetMs - a.TimeMs) / (b.TimeMs - a.TimeMs);

                x = (float)(a.X + (b.X - a.X) * ratio);
                z = (float)(a.Z + (b.Z - a.Z) * ratio);
                return;
            }
        }

        // 여기까지 왔다 = targetMs 가 가장 오래된 줄보다도 이전 -> 가장 오래된 줄의 위치
        x = History[0].X;
        z = History[0].Z;
    }
}
