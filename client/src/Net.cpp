#include "Net.h"
#include <iostream>

using boost::asio::ip::tcp;

Net::Net() : _socket(_io), _connected(false)
{
}

Net::~Net()
{
	// 소켓을 닫아야 받는 스레드의 read 가 오류로 끝난다. 그 뒤에 스레드를 기다린다
	// (shutdown 만으로는 안 끝남: 서버가 안 닫아 주면 join 에서 멈추는 것을 실험으로 확인)
	boost::system::error_code ec;
	_socket.shutdown(tcp::socket::shutdown_both, ec);
	_socket.close(ec);
	if (_recvThread.joinable())
	{
		_recvThread.join();
	}
}

bool Net::connect(const std::string& host, uint16_t port)
{
	boost::system::error_code ec;
	tcp::endpoint endpoint(boost::asio::ip::make_address(host, ec), port);
	if (ec)
	{
		std::cout << "[주소 오류] " << ec.message() << "\n";
		return false;
	}

	_socket.connect(endpoint, ec);
	if (ec)
	{
		std::cout << "[접속 실패] " << ec.message() << "\n";
		return false;
	}

	_socket.set_option(tcp::no_delay(true), ec);
	_connected = true;
	_recvThread = std::thread(&Net::recvLoop, this);
	return true;
}

bool Net::isConnected() const
{
	return _connected;
}

// [2B 길이][body] 로 한 번에 보낸다
void Net::send(const PacketBuffer& packet)
{
	if (!_connected)
	{
		return;
	}

	const std::vector<uint8_t>& body = packet.data();
	std::vector<uint8_t> frame;
	frame.push_back(static_cast<uint8_t>(body.size() >> 8));
	frame.push_back(static_cast<uint8_t>(body.size() & 0xFF));
	frame.insert(frame.end(), body.begin(), body.end());

	boost::system::error_code ec;
	boost::asio::write(_socket, boost::asio::buffer(frame), ec);
	if (ec)
	{
		_connected = false;
	}
}

// 그동안 받은 패킷을 out 으로 전부 옮긴다
void Net::poll(std::vector<PacketBuffer>& out)
{
	std::lock_guard<std::mutex> lock(_mutex);
	out.swap(_received);
	_received.clear();
}

// 받는 스레드: [2B 길이] 를 다 받고, 그 길이만큼 body 를 다 받는다 (서버 Session.cs 의 RunAsync 와 같은 순서)
void Net::recvLoop()
{
	while (true)
	{
		boost::system::error_code ec;
		uint8_t header[2];
		boost::asio::read(_socket, boost::asio::buffer(header), ec);
		if (ec)
		{
			break;
		}

		size_t len = (static_cast<size_t>(header[0]) << 8) | header[1];
		std::vector<uint8_t> body(len);
		boost::asio::read(_socket, boost::asio::buffer(body), ec);
		if (ec)
		{
			break;
		}

		std::lock_guard<std::mutex> lock(_mutex);
		_received.push_back(PacketBuffer(body));
	}

	_connected = false;
	std::cout << "[연결 끊김]\n";
}
