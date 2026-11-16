#include <algorithm>
#include <cmath>
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
	float y;   // 점프 높이
	float z;
	uint16_t pingMs;
	uint8_t hp;
	uint8_t flags;   // 1 = 죽어 있음, 2 = 무적
	uint8_t kills;
};

const uint8_t FlagDead = 1;
const uint8_t FlagInvincible = 2;

// 날아가는 총알 하나 (서버가 Shot 으로 알려 준 선)
struct Tracer
{
	glm::vec2 from;   // 쏜 자리 (x, z)
	glm::vec2 to;     // 멈춘 자리: 상대 몸 앞 또는 맵 끝
	sf::Clock age;    // 받은 뒤 흐른 시간
	bool mine;
};

const float ShootIntervalSec = 0.52f;   // 누르고 있을 때 연사 간격. 서버(0.5초)보다 조금 길게: 딱 맞추면 간발의 차로 서버에서 버려질 수 있음
const float TracerSpeed = 150.0f;       // 총알이 보이는 속도 (m/초). 판정은 쏜 순간 이미 끝났고, 이건 보여 주기용
const float TracerLength = 5.0f;        // 총알 선의 길이 (m)
const float TracerHeight = 1.0f;        // 총알이 날아가는 높이 (m)

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
	if (sf::Keyboard::isKeyPressed(sf::Keyboard::Key::Space))
	{
		bits |= 16;   // 점프
	}
	return bits;
}

// 마우스가 가리키는 조준점 (x, z) 을 구한다
// 바닥만 보면 안 된다: 비스듬히 내려다보기 때문에, 원기둥의 몸통을 클릭하면 그 "뒤쪽 바닥"이 찍혀서 빗나간다
//   (계산: 30m 떨어진 상대의 허리 높이 0.9m 를 찍으면 바닥 점은 2.2m 뒤 -> 쏜 직선이 상대 중심에서 2.2m 빗나감)
// 그래서 원기둥을 높이별로 얇게 잘라, 위에서부터 "마우스 직선이 그 높이에서 상대 몸통 안을 지나는가"를 본다
// 지나면 그 점이 조준점, 어느 높이에서도 안 지나면 바닥 점이 조준점
bool findAim(const Renderer& renderer, float mouseX, float mouseY, int width, int height, const std::vector<PlayerState>& players, uint32_t myId, glm::vec2& out)
{
	const float playerRadius = 0.5f;   // Renderer.cpp 의 원기둥과 같은 값
	const float playerHeight = 1.8f;
	const float sliceStep = 0.2f;

	for (float h = playerHeight + 1.2f; h > 0.0f; h -= sliceStep)   // 1.2 = 점프 최고 높이 (떠 있는 상대의 머리까지)
	{
		glm::vec2 point;
		if (!renderer.screenToPlane(mouseX, mouseY, width, height, h, point))
		{
			continue;
		}
		for (const PlayerState& p : players)
		{
			if (p.id == myId || (p.flags & FlagDead) != 0)
			{
				continue;
			}
			if (h < p.y || h > p.y + playerHeight)
			{
				continue;   // 이 높이는 그 사람 몸(발 p.y ~ 머리 p.y + 1.8)이 아니다
			}
			float dx = point.x - p.x;
			float dz = point.y - p.z;
			if (dx * dx + dz * dz < playerRadius * playerRadius)
			{
				out = point;
				return true;
			}
		}
	}
	return renderer.screenToPlane(mouseX, mouseY, width, height, 0.0f, out);
}

// 받은 패킷 하나를 읽어 myId / myPos / players 를 갱신
// myPos = 내 실제 위치, players 의 x, z = 유령 위치 (각자의 핑만큼 과거. 내 유령도 들어 있다)
// RoundEnd 를 받으면 winnerId 를 채우고 winnerClock 을 0 으로 (승리 문구를 몇 초 보여 주려고)
// Shot 을 받으면 tracers 에 총알 하나를 추가
void handlePacket(PacketBuffer& packet, uint32_t& myId, glm::vec3& myPos, std::vector<PlayerState>& players, uint32_t& winnerId, sf::Clock& winnerClock, std::vector<Tracer>& tracers)
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
		myPos.x = packet.readF32();
		myPos.y = packet.readF32();
		myPos.z = packet.readF32();
		uint16_t count = packet.readU16();
		players.clear();
		for (uint16_t i = 0; i < count; i++)
		{
			PlayerState p;
			p.id = packet.readU32();
			p.x = packet.readF32();
			p.y = packet.readF32();
			p.z = packet.readF32();
			p.pingMs = packet.readU16();
			p.hp = packet.readU8();
			p.flags = packet.readU8();
			p.kills = packet.readU8();
			players.push_back(p);
		}
	}
	else if (id == PacketId::RoundEnd)
	{
		winnerId = packet.readU32();
		winnerClock.restart();
		std::cout << "[승리] player " << winnerId << "\n";
	}
	else if (id == PacketId::Shot)
	{
		Tracer tracer;
		uint32_t shooterId = packet.readU32();
		tracer.from.x = packet.readF32();
		tracer.from.y = packet.readF32();
		tracer.to.x = packet.readF32();
		tracer.to.y = packet.readF32();
		packet.readU8();   // hit: 아직 안 씀
		tracer.mine = (shooterId == myId);
		tracers.push_back(tracer);
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
	sf::Text banner(font, L"", 34);   // 화면 가운데 위: 사망 / 승리 문구

	Renderer renderer;

	// ---------------------------------- 접속 ----------------------------------
	Net net;
	net.connect("127.0.0.1", 7777);

	uint32_t myId = 0;
	glm::vec3 myPos(0.0f, 0.0f, 0.0f);   // 내 실제 위치 (x, 점프 높이, z)
	std::vector<PlayerState> players;
	std::vector<PacketBuffer> packets;
	uint8_t lastKeyBits = 0;
	glm::vec2 aim(0.0f, 0.0f);   // 마우스가 가리키는 바닥 좌표 (x, z)
	uint32_t winnerId = 0;       // 마지막으로 이긴 사람 (0 = 아직 없음)
	sf::Clock winnerClock;       // 승리 문구를 띄운 뒤 흐른 시간
	std::vector<Tracer> tracers; // 지금 날아가고 있는 총알들
	sf::Clock shootClock;        // 마지막으로 쏜 뒤 흐른 시간
	bool patrol = false;         // 자동 왕복 중인가 (R 로 켜고 끔). 영상 촬영용: 창을 안 보고 있어도 좌우로 계속 걸음
	sf::Clock patrolClock;       // 자동 왕복을 켠 뒤 흐른 시간

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
			else if (const sf::Event::KeyPressed* key = event->getIf<sf::Event::KeyPressed>())
			{
				if (key->code == sf::Keyboard::Key::R)
				{
					patrol = !patrol;
					patrolClock.restart();
				}
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
		if (patrol)
		{
			// 자동 왕복: 1.5초 왼쪽(A = 2), 1.5초 오른쪽(D = 8)을 반복. 창이 선택 안 돼 있어도 동작
			// fmod(흐른 초, 3) = 3초 주기 안에서 몇 초째인가. 0~1.5 면 왼쪽, 1.5~3 이면 오른쪽
			float phase = std::fmod(patrolClock.getElapsedTime().asSeconds(), 3.0f);
			keyBits = (phase < 1.5f) ? 2 : 8;
		}
		if (keyBits != lastKeyBits)
		{
			PacketBuffer input(PacketId::Input);
			input.writeU8(keyBits);
			net.send(input);
			lastKeyBits = keyBits;
		}

		// 사격: 왼쪽 버튼을 누르고 있는 동안 ShootIntervalSec 마다 보낸다 (한 번 클릭해도 한 발은 나감)
		// 마우스가 가리키는 곳이 상대 몸통이면 그 자리, 아니면 바닥. 둘 다 아니면(하늘) 안 보냄
		sf::Vector2i shootMouse = sf::Mouse::getPosition(window);
		sf::Vector2u shootSize = window.getSize();
		bool mouseInWindow = shootMouse.x >= 0 && shootMouse.y >= 0 && shootMouse.x < static_cast<int>(shootSize.x) && shootMouse.y < static_cast<int>(shootSize.y);
		if (window.hasFocus() && mouseInWindow && sf::Mouse::isButtonPressed(sf::Mouse::Button::Left) && shootClock.getElapsedTime().asSeconds() >= ShootIntervalSec)
		{
			if (findAim(renderer, static_cast<float>(shootMouse.x), static_cast<float>(shootMouse.y), static_cast<int>(shootSize.x), static_cast<int>(shootSize.y), players, myId, aim))
			{
				PacketBuffer shoot(PacketId::Shoot);
				shoot.writeF32(aim.x);
				shoot.writeF32(aim.y);
				net.send(shoot);
				shootClock.restart();
			}
		}

		// 받기: 그동안 쌓인 패킷을 전부 처리
		net.poll(packets);
		for (PacketBuffer& packet : packets)
		{
			// 패킷이 규약보다 짧으면 읽다가 예외가 난다 (서버와 클라의 규약이 어긋났을 때). 그 패킷만 버림
			try
			{
				handlePacket(packet, myId, myPos, players, winnerId, winnerClock, tracers);
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

		// 내 상태 찾기 (죽었는지, 무적인지)
		bool iAmDead = false;
		bool iAmInvincible = false;
		for (const PlayerState& p : players)
		{
			if (p.id == myId)
			{
				iAmDead = (p.flags & FlagDead) != 0;
				iAmInvincible = (p.flags & FlagInvincible) != 0;
			}
		}

		// 보이는 규칙: 불투명 = 실제 위치, 반투명 = 유령(핑만큼 과거)
		// 불투명한 것 먼저: 무적인 상대(무적인 동안은 실제 위치가 보임 = 빨강), 나(실제 위치 = 초록)
		// 죽어 있는 사람은 안 그린다
		for (const PlayerState& p : players)
		{
			if (p.id != myId && (p.flags & FlagDead) == 0 && (p.flags & FlagInvincible) != 0)
			{
				renderer.drawPlayer(p.x, p.y, p.z, glm::vec4(0.90f, 0.35f, 0.30f, 1.0f));
			}
		}
		if (!players.empty() && !iAmDead)   // 첫 State 를 받기 전에는 내 위치를 모른다 (안 그러면 맵 가운데에 잠깐 그려짐)
		{
			renderer.drawPlayer(myPos.x, myPos.y, myPos.z, glm::vec4(0.30f, 0.85f, 0.45f, 1.0f));
		}

		// 총알: 쏜 자리에서 멈춘 자리까지 빠르게 날아가는 짧은 선. 끝까지 가면 지운다
		for (size_t i = 0; i < tracers.size(); )
		{
			const Tracer& tracer = tracers[i];
			glm::vec2 path = tracer.to - tracer.from;
			float pathLength = glm::length(path);
			float head = tracer.age.getElapsedTime().asSeconds() * TracerSpeed;   // 총알 머리가 간 거리
			float tail = head - TracerLength;                                     // 총알 꼬리가 간 거리
			if (pathLength < 0.001f || tail > pathLength)
			{
				tracers.erase(tracers.begin() + i);
				continue;
			}
			glm::vec2 dir = path / pathLength;
			glm::vec2 a = tracer.from + dir * std::max(tail, 0.0f);
			glm::vec2 b = tracer.from + dir * std::min(head, pathLength);
			glm::vec4 color = tracer.mine ? glm::vec4(1.00f, 0.95f, 0.55f, 1.0f) : glm::vec4(1.00f, 0.55f, 0.35f, 1.0f);
			renderer.drawLine(glm::vec3(a.x, TracerHeight, a.y), glm::vec3(b.x, TracerHeight, b.y), color, 4.0f);
			i++;
		}

		// 반투명한 것은 그 뒤에: 상대의 유령(반투명 빨강), "상대가 보는 나"(내 유령 = 반투명 초록)
		// 내가 무적이면 상대에게도 실제 위치가 보이므로 내 유령은 따로 안 그린다
		for (const PlayerState& p : players)
		{
			if ((p.flags & FlagDead) != 0)
			{
				continue;
			}
			if (p.id != myId && (p.flags & FlagInvincible) == 0)
			{
				renderer.drawGhost(p.x, p.y, p.z, glm::vec4(0.90f, 0.35f, 0.30f, 0.55f));
			}
			else if (p.id == myId && !iAmInvincible)
			{
				renderer.drawGhost(p.x, p.y, p.z, glm::vec4(0.30f, 0.85f, 0.45f, 0.35f));
			}
		}

		// 조준점: 마우스가 가리키는 바닥 자리에 반투명 노란 원기둥. 반투명은 불투명(플레이어) 뒤에 그린다
		sf::Vector2i mouse = sf::Mouse::getPosition(window);
		if (findAim(renderer, static_cast<float>(mouse.x), static_cast<float>(mouse.y), static_cast<int>(size.x), static_cast<int>(size.y), players, myId, aim))
		{
			renderer.drawGhost(aim.x, 0.0f, aim.y, glm::vec4(0.95f, 0.85f, 0.25f, 0.5f));
		}
		renderer.endFrame();

		// 그리기 2: SFML 로 글자 (3D 위에 덮어 그림). push/pop 사이에서만 SFML 그리기를 쓴다
		std::wstring text;
		if (net.isConnected())
		{
			text = L"내 번호 " + std::to_wstring(myId) + L"   플레이어 " + std::to_wstring(players.size()) + L"명";
			if (patrol)
			{
				text += L"   [자동 왕복 중: R 로 끄기]";
			}
			for (const PlayerState& p : players)
			{
				text += (p.id == myId) ? L"\n나   " : L"\n상대 ";
				text += L"핑 " + std::to_wstring(p.pingMs) + L"ms   HP " + std::to_wstring(p.hp) + L"   킬 " + std::to_wstring(p.kills);
			}
		}
		else
		{
			text = L"서버에 연결 안 됨 (127.0.0.1:7777)";
		}
		hud.setString(text);

		// 가운데 위 문구: 방금 누가 이겼으면 3초 동안 승리, 아니면 죽어 있는 사람
		std::wstring bannerText;
		if (winnerId != 0 && winnerClock.getElapsedTime().asSeconds() < 3.0f)
		{
			bannerText = L"player " + std::to_wstring(winnerId) + L" 승리";
		}
		else
		{
			for (const PlayerState& p : players)
			{
				if ((p.flags & FlagDead) != 0)
				{
					bannerText = L"player " + std::to_wstring(p.id) + L" 사망";
				}
			}
		}
		banner.setString(bannerText);
		banner.setPosition({ (static_cast<float>(size.x) - banner.getLocalBounds().size.x) / 2.0f, 40.0f });

		window.pushGLStates();
		if (iAmDead)
		{
			// 죽어 있는 동안: 화면 전체에 회색을 덮는다 (주변은 계속 보임)
			sf::RectangleShape gray(sf::Vector2f(static_cast<float>(size.x), static_cast<float>(size.y)));
			gray.setFillColor(sf::Color(120, 120, 120, 130));
			window.draw(gray);
		}
		window.draw(hud);
		window.draw(banner);
		window.popGLStates();

		window.display();
	}

	return 0;
}
