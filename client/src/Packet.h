#pragma once
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

// 패킷 포맷: [2B body length][2B packet_id][...payload...]
// PacketBuffer 는 body 전체(id 포함)를 다룬다.
enum class PacketId : uint16_t
{
    Welcome = 1,    // S2C: 접속 시 내 번호 (u32 myId)
    Input = 2,      // C2S: 키 상태, 바뀔 때만 (u8 keyBits: W=1, A=2, S=4, D=8)
    Shoot = 3,      // C2S: 사격 (f32 aimX, f32 aimZ)
    State = 4,      // S2C: 전원 상태 (u32 serverMs, u16 count, [u32 id, f32 x, f32 z, u16 pingMs, u8 hp]...)
    Shot = 5,       // S2C: 사격 결과 (u32 shooterId, f32 fromX, f32 fromZ, f32 toX, f32 toZ, u8 hit)
    RoundEnd = 6,   // S2C: 라운드 끝 (u32 winnerId)
};

class PacketBuffer
{
private:
    std::vector<uint8_t> _buf;
    size_t _readPos;

public:
    // 송신용: 패킷 ID 로 시작
    explicit PacketBuffer(PacketId id);
    
    // 수신용: Session/NetworkClient 가 받은 body bytes
    explicit PacketBuffer(const std::vector<uint8_t>& data);

    // ---------------------------------- 읽기 ----------------------------------
    PacketId readId();
    uint8_t readU8();
    uint16_t readU16();
    uint32_t readU32();
    float readF32();
    std::string readStr();

    // ---------------------------------- 쓰기 ----------------------------------
    void writeU8(uint8_t v);
    void writeU16(uint16_t v);
    void writeU32(uint32_t v);
    void writeF32(float v);
    void writeStr(const std::string& s);

    const std::vector<uint8_t>& data() const;

private:
    uint8_t next();
};
