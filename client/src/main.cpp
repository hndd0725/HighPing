#include <cstdint>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
#include "Net.h"   // boost.asio(windows.h)를 glad 보다 먼저: APIENTRY 중복 경고 방지
#include <glad/glad.h>
#include <SFML/Graphics.hpp>
#include "Packet.h"
#include "Renderer.h"

// State 패킷 안의 플레이어 한 명
struct PlayerState
{
	uint32_t id;
	float x;
	float z;
	uint16_t pingMs;
	uint8_t hp;
};

// 지금 눌린 키를 서버 규약의 비트로: W=1, A=2, S=4, D=8
uint8_t readKeyBits()
{
	uint8_t bits = 0;
	if (sf::Keyboard::isKeyPressed(sf::Keyboard::Key::W))
	{
		bits |= 1;
	}
	if (sf::Keyboard::isKeyPressed(sf::Keyboard::Key::A))
	{
		bits |= 2;
	}
	if (sf::Keyboard::isKeyPressed(sf::Keyboard::Key::S))
	{
		bits |= 4;
	}
	if (sf::Keyboard::isKeyPressed(sf::Keyboard::Key::D))
	{
		bits |= 8;
	}
	return bits;
}

// 받은 패킷 하나를 읽어 myId / players 를 갱신
void handlePacket(PacketBuffer& packet, uint32_t& myId, std::vector<PlayerState>& players)
{
	PacketId id = packet.readId();
	if (id == PacketId::Welcome)
	{
		myId = packet.readU32();
		std::cout << "[입장] 내 번호 " << myId << "\n";
	}
	else if (id == PacketId::State)
	{
		packet.readU32();   // serverMs: 아직 안 씀
		uint16_t count = packet.readU16();
		players.clear();
		for (uint16_t i = 0; i < count; i++)
		{
			PlayerState p;
			p.id = packet.readU32();
			p.x = packet.readF32();
			p.z = packet.readF32();
			p.pingMs = packet.readU16();
			p.hp = packet.readU8();
			players.push_back(p);
		}
	}
}

int main()
{
	// 소스가 UTF-8 이라 콘솔도 UTF-8 로 맞춰야 한글 로그가 안 깨진다 (Windows 콘솔 기본은 CP949)
	SetConsoleOutputCP(CP_UTF8);

	// ---------------------------------- 창 ----------------------------------
	sf::ContextSettings settings;
	settings.depthBits = 24;      // 깊이 버퍼: 3D 에서 앞뒤를 가리는 데 필요
	settings.majorVersion = 3;
	settings.minorVersion = 3;

	sf::RenderWindow window(sf::VideoMode({ 1280, 720 }), "HIGH PING", sf::Style::Default, sf::State::Windowed, settings);
	window.setVerticalSyncEnabled(true);

	if (!gladLoadGL())
	{
		std::cout << "[오류] OpenGL 함수를 불러오지 못함\n";
		return 1;
	}
	std::cout << "OpenGL " << glGetString(GL_VERSION) << "\n";

	sf::Font font;
	if (!font.openFromFile("C:/Windows/Fonts/malgun.ttf"))
	{
		std::cout << "[오류] 글꼴을 열지 못함\n";
		return 1;
	}
	sf::Text hud(font, L"", 20);
	hud.setPosition({ 12.0f, 8.0f });

	Renderer renderer;

	// ---------------------------------- 접속 ----------------------------------
	Net net;
	net.connect("127.0.0.1", 7777);

	uint32_t myId = 0;
	std::vector<PlayerState> players;
	std::vector<PacketBuffer> packets;
	uint8_t lastKeyBits = 0;

	// ---------------------------------- 반복: 입력 -> 받기 -> 그리기 ----------------------------------
	// 창은 여기서 닫지 않는다: 닫으면 OpenGL 도 같이 사라져서, 남은 그리기와 Renderer 정리가 갈 곳이 없어짐
	// (반복만 끝내면 main 이 끝날 때 Renderer -> 창 순서로 정리됨)
	bool running = true;
	while (running)
	{
		while (const std::optional<sf::Event> event = window.pollEvent())
		{
			if (event->is<sf::Event::Closed>())
			{
				running = false;
			}
			else if (const sf::Event::Resized* resized = event->getIf<sf::Event::Resized>())
			{
				// 글자가 창 크기에 맞춰 늘어나지 않게: SFML 이 그리는 영역을 새 크기로
				sf::Vector2f newSize(static_cast<float>(resized->size.x), static_cast<float>(resized->size.y));
				window.setView(sf::View(sf::FloatRect({ 0.0f, 0.0f }, newSize)));
			}
		}

		// 입력: 키 상태가 바뀔 때만 보낸다 (창이 선택돼 있을 때만 키를 읽음)
		uint8_t keyBits = window.hasFocus() ? readKeyBits() : 0;
		if (keyBits != lastKeyBits)
		{
			PacketBuffer input(PacketId::Input);
			input.writeU8(keyBits);
			net.send(input);
			lastKeyBits = keyBits;
		}

		// 받기: 그동안 쌓인 패킷을 전부 처리
		net.poll(packets);
		for (PacketBuffer& packet : packets)
		{
			// 패킷이 규약보다 짧으면 읽다가 예외가 난다 (서버와 클라의 규약이 어긋났을 때). 그 패킷만 버림
			try
			{
				handlePacket(packet, myId, players);
			}
			catch (const std::out_of_range& e)
			{
				std::cout << "[패킷 오류] " << e.what() << "\n";
			}
		}
		packets.clear();

		// 그리기 1: OpenGL 로 3D
		sf::Vector2u size = window.getSize();
		renderer.beginFrame(static_cast<int>(size.x), static_cast<int>(size.y));
		renderer.drawFloor();
		for (const PlayerState& p : players)
		{
			if (p.id == myId)
			{
				renderer.drawPlayer(p.x, p.z, glm::vec4(0.30f, 0.85f, 0.45f, 1.0f));   // 나: 초록
			}
			else
			{
				renderer.drawPlayer(p.x, p.z, glm::vec4(0.90f, 0.35f, 0.30f, 1.0f));   // 상대: 빨강
			}
		}
		renderer.endFrame();

		// 그리기 2: SFML 로 글자 (3D 위에 덮어 그림). push/pop 사이에서만 SFML 그리기를 쓴다
		std::wstring text;
		if (net.isConnected())
		{
			text = L"내 번호 " + std::to_wstring(myId) + L"   플레이어 " + std::to_wstring(players.size()) + L"명";
		}
		else
		{
			text = L"서버에 연결 안 됨 (127.0.0.1:7777)";
		}
		hud.setString(text);

		window.pushGLStates();
		window.draw(hud);
		window.popGLStates();

		window.display();
	}

	return 0;
}
