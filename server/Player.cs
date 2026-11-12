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

    public Player(Session session, float x, float z)
    {
        Id = session.Id;
        Session = session;
        X = x;
        Z = z;
        KeyBits = 0;
        PingMs = 200;
        Hp = 3;
    }
}
