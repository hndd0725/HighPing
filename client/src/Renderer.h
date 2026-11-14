#pragma once
#include <vector>
#include <glad/glad.h>
#include <glm/glm.hpp>

// OpenGL 로 3D 를 그리는 곳: 바닥, 격자, 플레이어(원기둥)
// 좌표: 바닥이 (x, z) 평면, 위쪽이 +y. 서버와 같은 단위(m)
class Renderer
{
private:
    // 그릴 물체 하나: 정점들이 GPU 에 올라가 있는 묶음
    struct Mesh
    {
        GLuint vao;
        GLuint vbo;
        GLsizei vertexCount;
        GLenum mode;   // GL_TRIANGLES 또는 GL_LINES
    };

    GLuint _program;
    GLint _uViewProj;
    GLint _uOffset;
    GLint _uColor;
    Mesh _floor;
    Mesh _grid;
    Mesh _cylinder;
    glm::mat4 _viewProj;

public:
    Renderer();
    ~Renderer();

    void beginFrame(int width, int height);
    void drawFloor();
    void drawPlayer(float x, float z, const glm::vec4& color);
    void drawGhost(float x, float z, const glm::vec4& color);
    void endFrame();
    bool screenToFloor(float mouseX, float mouseY, int width, int height, glm::vec2& out) const;

private:
    glm::mat4 makeViewProj(float aspect) const;
    Mesh makeMesh(const std::vector<float>& vertices, GLenum mode);
    void drawMesh(const Mesh& mesh, const glm::vec3& offset, const glm::vec4& color);
};
