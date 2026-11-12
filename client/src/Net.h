#pragma once
#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <boost/asio.hpp>
#include "Packet.h"

// 서버와의 연결 1개.
// 받기: 받는 스레드 하나가 패킷을 읽어 _received 에 쌓기만 한다 (서버의 Session 과 같은 생각)
// 꺼내기: 메인 스레드가 매 프레임 poll() 로 한꺼번에 가져간다
// 보내기: 메인 스레드가 send() 로 바로 보낸다
class Net
{
private:
    boost::asio::io_context _io;
    boost::asio::ip::tcp::socket _socket;
    std::thread _recvThread;
    std::mutex _mutex;                      // _received 를 두 스레드가 만지므로
    std::vector<PacketBuffer> _received;
    std::atomic<bool> _connected;

public:
    Net();
    ~Net();

    bool connect(const std::string& host, uint16_t port);
    bool isConnected() const;
    void send(const PacketBuffer& packet);
    void poll(std::vector<PacketBuffer>& out);

private:
    void recvLoop();
};
