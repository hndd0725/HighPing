#include "Packet.h"

PacketBuffer::PacketBuffer(PacketId id) : _readPos(0)
{
	writeU16(static_cast<uint16_t>(id));
}

PacketBuffer::PacketBuffer(const std::vector<uint8_t>& data) : _buf(data), _readPos(0)
{
}

// ---------------------------------- 읽기 ----------------------------------
PacketId PacketBuffer::readId()
{
	return static_cast<PacketId>(readU16());
}

uint8_t PacketBuffer::readU8()
{
	return next();
}

uint16_t PacketBuffer::readU16()
{
	uint16_t v = static_cast<uint16_t>(next()) << 8; 
	
	v |= next(); 
	
	return v;
}

uint32_t PacketBuffer::readU32()
{
	uint32_t v = 0; 
	
	for (int i = 3; i >= 0; --i)
	{
		v |= static_cast<uint32_t>(next()) << (8 * i);
	}
	
	return v;
}

float PacketBuffer::readF32()
{
	uint32_t u = readU32(); 
	float f; 
	std::memcpy(&f, &u, 4); 

	return f;
}

std::string PacketBuffer::readStr()
{
	uint16_t len = readU16(); 
	std::string s(len, '\0'); 
	for (char& c : s)
	{
		c = static_cast<char>(next());
	}
	
	return s;
}

// ---------------------------------- 쓰기 ----------------------------------
void PacketBuffer::writeU8(uint8_t v)
{
	_buf.push_back(v);
}

void PacketBuffer::writeU16(uint16_t v)
{
	_buf.push_back(static_cast<uint8_t>(v >> 8)); 
	_buf.push_back(static_cast<uint8_t>(v & 0xFF));
}

void PacketBuffer::writeU32(uint32_t v)
{
	for (int i = 3; i >= 0; --i)
	{
		_buf.push_back(static_cast<uint8_t>((v >> (8 * i)) & 0xFF));
	}
}

void PacketBuffer::writeF32(float v)
{
	uint32_t u; 
	std::memcpy(&u, &v, 4); 
	writeU32(u);
}

void PacketBuffer::writeStr(const std::string& s)
{
	writeU16(static_cast<uint16_t>(s.size())); 
	for (char c : s)
	{
		_buf.push_back(static_cast<uint8_t>(c));

	}
}

const std::vector<uint8_t>& PacketBuffer::data() const
{
	return _buf;
}

uint8_t PacketBuffer::next()
{
	if (_readPos >= _buf.size())
	{
		throw std::out_of_range("PacketBuffer underflow");
	}
	
	return _buf[_readPos++];
}

