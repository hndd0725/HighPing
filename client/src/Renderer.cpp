#include "Renderer.h"
#include <cmath>
#include <iostream>
#include <glm/gtc/type_ptr.hpp>
#include <glm/gtc/matrix_transform.hpp>

namespace
{
	const float HalfWidth = 20.0f;    // 맵 40 x 30 (서버 GameWorld.cs 와 같은 값)
	const float HalfHeight = 15.0f;
	const float PlayerRadius = 0.5f;
	const float PlayerHeight = 1.8f;
	const int CylinderSides = 24;

	// 정점 하나 = 위치(x, y, z) + 법선(nx, ny, nz). 법선 = 그 면이 바라보는 방향 (밝기 계산용)
	const char* VertexShader = R"(
		#version 330 core
		layout(location = 0) in vec3 aPos;
		layout(location = 1) in vec3 aNormal;
		uniform mat4 uViewProj;
		uniform vec3 uOffset;
		out vec3 vNormal;
		void main()
		{
			vNormal = aNormal;
			gl_Position = uViewProj * vec4(aPos + uOffset, 1.0);
		}
	)";

	// 빛을 정면으로 받는 면은 밝게, 비스듬한 면은 어둡게
	const char* FragmentShader = R"(
		#version 330 core
		in vec3 vNormal;
		uniform vec4 uColor;
		out vec4 outColor;
		void main()
		{
			vec3 lightDir = normalize(vec3(0.4, 1.0, 0.6));
			float light = 0.45 + 0.55 * max(dot(normalize(vNormal), lightDir), 0.0);
			outColor = vec4(uColor.rgb * light, uColor.a);
		}
	)";

	GLuint compileShader(GLenum type, const char* source)
	{
		GLuint shader = glCreateShader(type);
		glShaderSource(shader, 1, &source, nullptr);
		glCompileShader(shader);

		GLint ok = 0;
		glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
		if (!ok)
		{
			char log[1024];
			glGetShaderInfoLog(shader, sizeof(log), nullptr, log);
			std::cout << "[셰이더 컴파일 오류] " << log << "\n";
		}
		return shader;
	}

	void pushVertex(std::vector<float>& v, float x, float y, float z, float nx, float ny, float nz)
	{
		v.insert(v.end(), { x, y, z, nx, ny, nz });
	}

	// 바닥: y = 0 에 놓인 사각형 (삼각형 2개)
	std::vector<float> buildFloor()
	{
		std::vector<float> v;
		pushVertex(v, -HalfWidth, 0, -HalfHeight, 0, 1, 0);
		pushVertex(v, -HalfWidth, 0, HalfHeight, 0, 1, 0);
		pushVertex(v, HalfWidth, 0, HalfHeight, 0, 1, 0);
		pushVertex(v, -HalfWidth, 0, -HalfHeight, 0, 1, 0);
		pushVertex(v, HalfWidth, 0, HalfHeight, 0, 1, 0);
		pushVertex(v, HalfWidth, 0, -HalfHeight, 0, 1, 0);
		return v;
	}

	// 격자: 5m 마다 선 (거리감을 보려고)
	std::vector<float> buildGrid()
	{
		std::vector<float> v;
		const float y = 0.0f;
		for (float x = -HalfWidth; x <= HalfWidth; x += 5.0f)
		{
			pushVertex(v, x, y, -HalfHeight, 0, 1, 0);
			pushVertex(v, x, y, HalfHeight, 0, 1, 0);
		}
		for (float z = -HalfHeight; z <= HalfHeight; z += 5.0f)
		{
			pushVertex(v, -HalfWidth, y, z, 0, 1, 0);
			pushVertex(v, HalfWidth, y, z, 0, 1, 0);
		}
		return v;
	}

	// 원기둥: 옆면(사각형 24개를 둥글게) + 윗면(부채꼴 24개). 밑면은 바닥에 가려 안 보이므로 안 만듦
	std::vector<float> buildCylinder()
	{
		std::vector<float> v;
		const float twoPi = 6.2831853f;
		for (int i = 0; i < CylinderSides; i++)
		{
			float a0 = twoPi * i / CylinderSides;
			float a1 = twoPi * (i + 1) / CylinderSides;
			float c0 = std::cos(a0);
			float s0 = std::sin(a0);
			float c1 = std::cos(a1);
			float s1 = std::sin(a1);
			float x0 = c0 * PlayerRadius;
			float z0 = s0 * PlayerRadius;
			float x1 = c1 * PlayerRadius;
			float z1 = s1 * PlayerRadius;

			// 옆면: 법선은 중심에서 바깥쪽
			pushVertex(v, x0, 0, z0, c0, 0, s0);
			pushVertex(v, x0, PlayerHeight, z0, c0, 0, s0);
			pushVertex(v, x1, PlayerHeight, z1, c1, 0, s1);
			pushVertex(v, x0, 0, z0, c0, 0, s0);
			pushVertex(v, x1, PlayerHeight, z1, c1, 0, s1);
			pushVertex(v, x1, 0, z1, c1, 0, s1);

			// 윗면: 법선은 위쪽
			pushVertex(v, 0, PlayerHeight, 0, 0, 1, 0);
			pushVertex(v, x1, PlayerHeight, z1, 0, 1, 0);
			pushVertex(v, x0, PlayerHeight, z0, 0, 1, 0);
		}
		return v;
	}
}

Renderer::Renderer() : _program(0), _uViewProj(-1), _uOffset(-1), _uColor(-1), _floor(), _grid(), _cylinder(), _viewProj(1.0f)
{
	GLuint vs = compileShader(GL_VERTEX_SHADER, VertexShader);
	GLuint fs = compileShader(GL_FRAGMENT_SHADER, FragmentShader);
	_program = glCreateProgram();
	glAttachShader(_program, vs);
	glAttachShader(_program, fs);
	glLinkProgram(_program);
	glDeleteShader(vs);
	glDeleteShader(fs);

	GLint ok = 0;
	glGetProgramiv(_program, GL_LINK_STATUS, &ok);
	if (!ok)
	{
		char log[1024];
		glGetProgramInfoLog(_program, sizeof(log), nullptr, log);
		std::cout << "[셰이더 연결 오류] " << log << "\n";
	}

	_uViewProj = glGetUniformLocation(_program, "uViewProj");
	_uOffset = glGetUniformLocation(_program, "uOffset");
	_uColor = glGetUniformLocation(_program, "uColor");

	_floor = makeMesh(buildFloor(), GL_TRIANGLES);
	_grid = makeMesh(buildGrid(), GL_LINES);
	_cylinder = makeMesh(buildCylinder(), GL_TRIANGLES);
}

Renderer::~Renderer()
{
	const Mesh* meshes[] = { &_floor, &_grid, &_cylinder };
	for (const Mesh* mesh : meshes)
	{
		glDeleteBuffers(1, &mesh->vbo);
		glDeleteVertexArrays(1, &mesh->vao);
	}
	glDeleteProgram(_program);
}

// 3D 그리기 시작: 화면을 지우고, 이번 프레임의 카메라 행렬을 정한다
void Renderer::beginFrame(int width, int height)
{
	glViewport(0, 0, width, height);
	glEnable(GL_DEPTH_TEST);                             // 앞에 있는 것이 뒤의 것을 가리게
	glEnable(GL_BLEND);                                  // 반투명(유령)용
	glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
	glClearColor(0.05f, 0.06f, 0.09f, 1.0f);
	glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

	_viewProj = makeViewProj(static_cast<float>(width) / static_cast<float>(height));

	glUseProgram(_program);
	glUniformMatrix4fv(_uViewProj, 1, GL_FALSE, glm::value_ptr(_viewProj));
}

void Renderer::drawFloor()
{
	drawMesh(_floor, glm::vec3(0, 0, 0), glm::vec4(0.16f, 0.18f, 0.24f, 1.0f));

	// 격자는 바닥과 같은 높이라 깊이 비교를 하면 선이 군데군데 사라진다 -> 격자만 깊이 비교 없이 덮어 그림
	glDisable(GL_DEPTH_TEST);
	drawMesh(_grid, glm::vec3(0, 0, 0), glm::vec4(0.45f, 0.50f, 0.62f, 1.0f));
	glEnable(GL_DEPTH_TEST);
}

void Renderer::drawPlayer(float x, float z, const glm::vec4& color)
{
	drawMesh(_cylinder, glm::vec3(x, 0, z), color);
}

// 반투명한 것(조준점, 유령)용: 불투명한 것을 다 그린 뒤에 부른다
// 깊이 버퍼에 기록하지 않아서, 뒤에 있는 것이 이것 때문에 지워지지 않고 비쳐 보인다
void Renderer::drawGhost(float x, float z, const glm::vec4& color)
{
	glDepthMask(GL_FALSE);
	drawMesh(_cylinder, glm::vec3(x, 0, z), color);
	glDepthMask(GL_TRUE);
}

// 3D 그리기 끝: SFML 이 글자를 그릴 수 있게 우리가 묶어 둔 것들을 풀어 준다
void Renderer::endFrame()
{
	glBindVertexArray(0);
	glBindBuffer(GL_ARRAY_BUFFER, 0);
	glUseProgram(0);
}

// 카메라 행렬: 게임 좌표 (x, y, z) 를 화면 좌표(가로 -1~1, 세로 -1~1)로 바꾸는 4x4 행렬
//
// 쿼터뷰: lookAt(어디서 어디를 보나) + perspective(멀수록 작게). 값은 본인이 바꿔 보며 정함 (11/14)
glm::mat4 Renderer::makeViewProj(float aspect) const
{
	glm::vec3 eye(0 , 15 , 35 );
	glm::vec3 target(0.0f, 0.0f, 0.0f);
	glm::vec3 up(0.0f, 1.0f, 0.0f);
	glm::mat4 view = glm::lookAt(eye, target, up);
	glm::mat4 proj = glm::perspective(glm::radians(50.0f ), aspect, 0.1f, 200.0f);
	return proj * view;
}

// 마우스(화면 픽셀)가 가리키는, 높이 planeY 인 수평면 위의 좌표 (x, z) 를 구한다 (planeY = 0 이면 바닥)
// 생각: 카메라에서 마우스 방향으로 직선을 쏴서, 그 직선이 그 높이와 만나는 점
// 만나는 점이 없으면(마우스가 지평선 위를 가리킴) false 를 돌려주고 out 은 그대로 둔다
bool Renderer::screenToPlane(float mouseX, float mouseY, int width, int height, float planeY, glm::vec2& out) const
{
	// 1) 픽셀 -> 화면 좌표(-1 ~ 1). 픽셀은 위가 0 이고 화면 좌표는 위가 +1 이라 세로는 뒤집는다
	float nx = 2.0f * mouseX / static_cast<float>(width) - 1.0f;
	float ny = 1.0f - 2.0f * mouseY / static_cast<float>(height);

	// 2) 카메라 행렬을 거꾸로 적용: 화면의 그 점을 지나는 직선 위의 두 점 (카메라에 가까운 점, 먼 점)
	glm::mat4 inverse = glm::inverse(_viewProj);
	glm::vec4 nearPoint = inverse * glm::vec4(nx, ny, -1.0f, 1.0f);
	glm::vec4 farPoint = inverse * glm::vec4(nx, ny, 1.0f, 1.0f);
	nearPoint /= nearPoint.w;
	farPoint /= farPoint.w;

	// 3) [★ 본인 빈칸] 직선 위의 점 = nearPoint + (farPoint - nearPoint) * t   (t = 0 이면 nearPoint, 1 이면 farPoint)
	//    이 점의 높이(y)가 planeY 가 되는 t 를 구한다. 쓸 값: nearPoint.y, farPoint.y
	//    (처음엔 바닥만 봐서 -nearPoint.y 였다. 몸통 높이에서도 쓰려고 planeY 를 넣음)
	// 직선이 아래로 내려가지 않으면 바닥과 안 만난다 (그대로 계산하면 t 가 음수가 되어 카메라 뒤쪽 점이 나옴)
	if (farPoint.y - nearPoint.y > -0.0001f)
	{
		return false;
	}
	float t = (planeY - nearPoint.y) / (farPoint.y - nearPoint.y);

	// 4) 그 t 를 넣으면 그 높이와 만나는 점
	glm::vec4 hit = nearPoint + (farPoint - nearPoint) * t;
	out = glm::vec2(hit.x, hit.z);
	return true;
}

// 정점 배열을 GPU 에 올리고, "한 정점은 float 6개(위치 3 + 법선 3)" 라고 알려 준다
Renderer::Mesh Renderer::makeMesh(const std::vector<float>& vertices, GLenum mode)
{
	Mesh mesh;
	mesh.vertexCount = static_cast<GLsizei>(vertices.size() / 6);
	mesh.mode = mode;

	glGenVertexArrays(1, &mesh.vao);
	glGenBuffers(1, &mesh.vbo);
	glBindVertexArray(mesh.vao);
	glBindBuffer(GL_ARRAY_BUFFER, mesh.vbo);
	glBufferData(GL_ARRAY_BUFFER, vertices.size() * sizeof(float), vertices.data(), GL_STATIC_DRAW);

	glEnableVertexAttribArray(0);
	glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), reinterpret_cast<void*>(0));
	glEnableVertexAttribArray(1);
	glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), reinterpret_cast<void*>(3 * sizeof(float)));

	glBindVertexArray(0);
	return mesh;
}

void Renderer::drawMesh(const Mesh& mesh, const glm::vec3& offset, const glm::vec4& color)
{
	glUniform3fv(_uOffset, 1, glm::value_ptr(offset));
	glUniform4fv(_uColor, 1, glm::value_ptr(color));
	glBindVertexArray(mesh.vao);
	glDrawArrays(mesh.mode, 0, mesh.vertexCount);
}
