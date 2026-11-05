using System.Text;

// 패킷 포맷: [2B body length][2B packet_id][...payload...]  (빅 엔디안)
// PacketBuffer 는 body 전체(id 포함)를 다룬다.
// 규약 원본: docs/기획_1주차.md 5번
enum PacketId : ushort
{
    Welcome = 1,   // S2C: u32 myId
    Input = 2,     // C2S: u8 keyBits (W=1, A=2, S=4, D=8), 바뀔 때만
    Shoot = 3,     // C2S: f32 aimX, f32 aimZ
    State = 4,     // S2C: u32 serverMs, u16 count, [u32 id, f32 x, f32 z, u16 pingMs, u8 hp]...
    Shot = 5,      // S2C: u32 shooterId, f32 fromX, fromZ, toX, toZ, u8 hit
    RoundEnd = 6,  // S2C: u32 winnerId
}

class PacketBuffer
{
    private readonly List<byte> _buf;
    private int _readPos;

    // 송신용: 패킷 ID 로 시작
    public PacketBuffer(PacketId id)
    {
        _buf = new List<byte>();
        _readPos = 0;
        WriteU16((ushort)id);
    }

    // 수신용: Session 이 받은 body bytes
    public PacketBuffer(byte[] data)
    {
        _buf = new List<byte>(data);
        _readPos = 0;
    }

    // ---------------------------------- 읽기 ----------------------------------
    public PacketId ReadId()
    {
        return (PacketId)ReadU16();
    }

    public byte ReadU8()
    {
        return Next();
    }

    public ushort ReadU16()
    {
        ushort v = (ushort)(Next() <<8);
        v |= Next();
        return v;
    }

    public uint ReadU32()
    {
        uint v = 0;
        for (int i = 3; i >= 0; --i)
        {
            v |= (uint)Next() << (8 * i);
        }
        return v;
    }

    public float ReadF32()
    {
        uint u = ReadU32();
        return BitConverter.UInt32BitsToSingle(u);   // C++의 memcpy(&f, &u, 4)
    }

    public string ReadStr()
    {
        ushort len = ReadU16();
        byte[] bytes = new byte[len];
        for (int i = 0; i < len; ++i)
        {
            bytes[i] = Next();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    // ---------------------------------- 쓰기 ----------------------------------
    public void WriteU8(byte v)
    {
        _buf.Add(v);
    }

    public void WriteU16(ushort v)
    {
        _buf.Add((byte)(v >> 8));
        _buf.Add((byte)(v & 0xFF));
    }

    public void WriteU32(uint v)
    {
        for (int i = 3; i >= 0; --i)
        {
            _buf.Add((byte)((v >> (8 * i)) & 0xFF));
        }
    }

    public void WriteF32(float v)
    {
        WriteU32(BitConverter.SingleToUInt32Bits(v));   // C++의 memcpy(&u, &v, 4)
    }

    public void WriteStr(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        WriteU16((ushort)bytes.Length);
        _buf.AddRange(bytes);
    }

    public byte[] Data()
    {
        return _buf.ToArray();
    }

    private byte Next()
    {
        if (_readPos>=_buf.Count)//몰라
        {
            throw new InvalidDataException("PacketBuffer underflow");
        }
        return _buf[_readPos++];
    }
}
