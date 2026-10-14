#pragma once

GLuint program(const char* vertex, const char* fragment) {
    GLuint shaders[2] = {glCreateShader(GL_VERTEX_SHADER), glCreateShader(GL_FRAGMENT_SHADER)};
    GLuint result = glCreateProgram();
    try {
        for (int i = 0; i < 2; i++) {
            const char* source = i ? fragment : vertex; glShaderSource(shaders[i], 1, &source, nullptr); glCompileShader(shaders[i]);
            GLint valid = 0; glGetShaderiv(shaders[i], GL_COMPILE_STATUS, &valid);
            if (!valid) { char log[1024] = {}; glGetShaderInfoLog(shaders[i], sizeof log, nullptr, log); __android_log_print(ANDROID_LOG_ERROR, "QFTCalibration", "%s", log); }
            require(valid, "Calibration shader compilation failed"); glAttachShader(result, shaders[i]);
        }
        glLinkProgram(result); GLint valid = 0; glGetProgramiv(result, GL_LINK_STATUS, &valid); require(valid, "Calibration shader link failed");
    } catch (...) { glDeleteProgram(result); for (auto shader : shaders) glDeleteShader(shader); throw; }
    for (auto shader : shaders) glDeleteShader(shader);
    return result;
}

const char* avatarVertex = R"glsl(#version 300 es
layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec3 a_Normal;
layout(location = 2) in vec3 a_Tangent;
layout(location = 3) in uvec4 a_Joints;
layout(location = 4) in vec4 a_Weights;
layout(location = 5) in vec4 a_Color;
layout(location = 6) in vec2 a_UV;
layout(location = 7) in vec4 a_ORMT;
layout(location = 8) in float a_Curvature;
layout(std140) uniform SkinBlock { mat4 u_Skin[256]; };
uniform mat4 u_Model, u_ViewProj;
out vec3 v_WorldPos, v_Normal, v_Tangent; out vec2 v_UV; out vec4 v_Color, v_ORMT; out float v_Curvature, v_Height;
void main() {
    ivec4 j = ivec4(a_Joints);
    mat4 skin = (u_Skin[j.x] * a_Weights.x + u_Skin[j.y] * a_Weights.y + u_Skin[j.z] * a_Weights.z + u_Skin[j.w] * a_Weights.w) / dot(a_Weights, vec4(1.0));
    mat4 m = u_Model * skin;
    vec4 world = m * vec4(a_Position, 1.0);
    v_WorldPos = world.xyz; v_Normal = normalize(mat3(m) * a_Normal); v_Tangent = normalize(mat3(m) * a_Tangent);
    v_UV = a_UV; v_Color = a_Color; v_ORMT = a_ORMT; v_Curvature = a_Curvature; v_Height = (skin * vec4(a_Position, 1.0)).y;
    gl_Position = u_ViewProj * world;
})glsl";
const char* avatarFragment = R"glsl(#version 300 es
precision highp float;
in vec3 v_WorldPos, v_Normal, v_Tangent; in vec2 v_UV; in vec4 v_Color, v_ORMT; in float v_Curvature, v_Height;
uniform sampler2D u_BaseColor, u_Normal, u_ORM;
uniform vec3 u_CameraPos, u_LightDir, u_LightColor, u_SkyColor, u_GroundColor;
uniform vec4 u_Hair[2], u_FacialHair[2];
uniform vec2 u_Bust;
out vec4 o_Color;
const float PI = 3.141592653589793, EPS = 0.001;
vec3 safeNormalize(vec3 v) { return v * inversesqrt(max(EPS, dot(v, v))); }
float avg3(vec3 v) { return (v.x + v.y + v.z) / 3.0; }
vec3 ambient(vec3 n) { return mix(u_GroundColor, u_SkyColor, n.y * 0.5 + 0.5); }
vec3 ambientSpecular(vec3 n, vec3 V, float rough, float metal, vec3 base, float occ) {
    vec3 env = mix(ambient(reflect(-V, n)), 0.5 * (u_SkyColor + u_GroundColor), rough) * occ;
    float r2 = max(rough * rough, 0.0078125); r2 *= r2;
    float grazing = clamp(2.0 - rough - 0.96 * (1.0 - metal), 0.0, 1.0);
    float fresnel = pow(1.0 - clamp(dot(n, V), 0.0, 1.0), 4.0);
    return env * (1.0 + mix(mix(vec3(0.04), base, metal), vec3(grazing), fresnel) / (r2 + 1.0));
}
vec3 dfgTerm(vec3 f0, float gloss, float NdotV) {
    float x = gloss, y = NdotV;
    float bias = clamp(min(-0.1688 * x + 1.895 * x * x, 0.9903 - 4.853 * y + 8.404 * y * y - 5.069 * y * y * y), 0.0, 1.0);
    float delta = clamp(0.6045 + 1.699 * x - 0.5228 * y - 3.603 * x * x + 1.404 * x * y + 0.1939 * y * y + 2.661 * x * x * x, 0.0, 1.0);
    return f0 * (delta - bias) + bias * clamp(50.0 * f0.y, 0.0, 1.0);
}
vec3 ggx(vec3 N, vec3 V, vec3 L, vec3 f0, float NdotL, float NdotV, float alphaG, vec3 dlc) {
    vec3 H = normalize(L + V); NdotL = clamp(NdotL, 0.0, 1.0);
    vec3 F = f0 + (1.0 - f0) * pow(1.0 - max(clamp(dot(V, H), 0.0, 1.0), 0.5), 5.0);
    vec3 NxH = cross(N, H); float c = 1.0 - dot(NxH, NxH), a2 = alphaG * alphaG;
    float d = max(c * c * (a2 - 1.0) + 1.0, 1e-10), D = min(a2 / (d * d), 55500.0);
    float Vis = 0.5 / max(0.05, mix(2.0 * NdotL * NdotV, NdotL + NdotV, alphaG));
    return F * D * Vis * NdotL * dlc;
}
vec3 sssPunctual(float NdotL, float c) {
    c = clamp(c, 0.0, 1.0);
    vec3 t = NdotL * (vec3(-0.00808314, 0.0835151, 0.0920432) * c + vec3(0.355864, 0.418737, 0.407632)) + (vec3(0.0241138, -0.0446712, -0.0382355) * c + vec3(0.622981, 0.562233, 0.546097));
    vec3 fade = clamp(vec3(-0.915144, -0.366103, -0.207718) * c + vec3(0.9084, 0.315669, 0.167824), 0.0, 1.0);
    return t * t * t * fade + clamp(NdotL, 0.0, 1.0) * (1.0 - fade);
}
vec3 sssAmbient(vec3 n, float c) {
    c = clamp(c, 0.0, 1.0);
    vec3 zh1 = vec3(c * c * c * 0.29126806 - c * c * 0.3900409 - c * 0.11944291 + 0.6474976, -c * c * 0.03049119 - c * 0.00234933 + 0.64157382, -c * c * 0.0137731528 + c * 0.000875828145 + 0.641376033);
    vec3 l0 = 0.5 * (u_SkyColor + u_GroundColor), l1 = 0.5 * (u_SkyColor - u_GroundColor);
    return max(l0 * 0.282095 + l1 * (0.488603 * zh1 * n.y), 0.0) * PI;
}
float hairLobe(float aniso, float rough, vec3 L, vec3 E, vec3 N, vec3 T, vec3 B, float offset) {
    vec3 H = E + L; N = normalize(N + B * offset);
    H = normalize(mix(normalize(H), normalize(H - dot(H, T) * T), aniso));
    return pow(max(0.0, dot(N, H)), 1.0 / (rough * rough + EPS));
}
void main() {
    float type = floor(v_Color.a * 255.0 + 0.5);
    bool isEye = type == 2.0, isSkin = type == 3.0, isFacialHair = type == 5.0, isHair = type == 4.0 || isFacialHair;
    vec3 N0 = normalize(v_Normal), T = normalize(v_Tangent), B = normalize(cross(T, N0));
    vec3 V = normalize(u_CameraPos - v_WorldPos), L = u_LightDir;
    vec3 base = texture(u_BaseColor, v_UV).rgb; vec4 orm = texture(u_ORM, v_UV);
    vec4 nm = texture(u_Normal, v_UV); vec2 xy = vec2(nm.g, 1.0 - nm.a) * 2.0 - 1.0;
    vec3 N = normalize(xy.x * T + xy.y * B + sqrt(max(1.0 - dot(xy, xy), 0.0)) * N0);
    float occ = orm.r, rough = orm.g, metal = orm.b, curvature = v_Curvature * 1.284 + 0.0186;
    vec4 h0 = isFacialHair ? u_FacialHair[0] : u_Hair[0], h1 = isFacialHair ? u_FacialHair[1] : u_Hair[1];
    vec3 hairColor = safeNormalize(base * base + EPS) * max(0.1, length(base));
    float hairBlend = 0.0, hairAniso = 0.0, flow = 0.0;
    if (isHair) {
        flow = ((1.0 - v_ORMT.g) - 0.25) * 2.0 * PI; hairBlend = clamp(orm.a * 2.0, 0.0, 1.0); hairAniso = clamp((orm.a - 0.5) * 2.0, 0.0, 1.0);
        rough = 0.4; metal = 0.0; N = mix(N0, N, hairBlend);
    }
    vec3 hT = normalize(T * cos(flow) + B * sin(flow)), hN = mix(N0, N, h1.y), hB = normalize(cross(hT, hN));
    float NdotV = clamp(dot(N, V), 0.0, 1.0), NdotL = dot(N, L);
    vec3 f0 = mix(vec3(0.04), base, metal); float alphaG = rough * rough + EPS;
    vec3 dfg = dfgTerm(f0, 1.0 - pow(alphaG, 0.25), NdotV);
    vec3 glint = vec3(0.0);
    if (isEye) glint = vec3(10.0 * pow(clamp(dot(N0, normalize(V + vec3(0.0, 0.25, 0.0) + 0.25 * cross(vec3(0.0, 1.0, 0.0), V))), 0.0, 1.0), 2000.0));
    vec3 dlc = u_LightColor / PI * clamp((dot(V, L) + 1.0) / 0.75, 0.0, 1.0);
    vec3 punctualDiffuse = clamp(NdotL, 0.0, 1.0) * (1.0 - avg3(dfg)) * dlc, punctualSSS = vec3(0.0), rim = vec3(0.0);
    if (isSkin) punctualSSS = sssPunctual(NdotL, curvature) * dlc;
    if (isHair) { float w = smoothstep(-0.5, 1.0, NdotL); punctualSSS = w * dlc / (1.0 + abs(NdotL - w)); }
    vec3 punctualSpec = ggx(N, V, L, f0, NdotL, NdotV, alphaG, dlc);
    vec3 ambDiffuse = ambient(N) * occ, ambSpec = ambientSpecular(N, V, rough, metal, base, occ);
    if (isHair) {
        float anisotropy = u_Hair[0].x * hairAniso, offset = u_Hair[1].z * (orm.b - 0.5) * hairAniso;
        vec3 dir = anisotropy >= 0.0 ? hT : hB, across = cross(cross(dir, V), dir);
        vec3 bent = normalize(mix(N, normalize(across + dir * offset), anisotropy * 0.75));
        vec3 spec = ambientSpecular(bent, V, u_Hair[0].z, 0.0, base, occ) * h1.w;
        bent = normalize(mix(N, normalize(across + dir * (offset + h1.x)), anisotropy * 0.75));
        spec += ambientSpecular(bent, V, u_Hair[0].y, 0.0, base, occ) * hairColor * h0.w;
        ambSpec = spec * hairBlend;
    }
    vec3 dc = (1.0 - metal) * base;
    ambDiffuse *= occ / ((1.0 - dc) + dc * occ);
    ambSpec *= clamp(pow(abs(NdotV + occ), exp2(-16.0 * rough - 1.0)) - 1.0 + occ, 0.0, 1.0) * dfg;
    if (isHair) {
        vec3 R = reflect(-V, N), toRay = dot(L, R) * R - L;
        vec3 Ls = normalize(L + toRay * clamp(0.174108138 / length(toRay), 0.0, 1.0));
        float aniso = h0.x * hairAniso, offset = h1.z * (orm.b - 0.5) * hairAniso;
        float white = hairLobe(aniso, h0.z, Ls, V, hN, hB, hT, offset) * h1.w, color = hairLobe(aniso, h0.y, Ls, V, hN, hB, hT, h1.x + offset) * h0.w;
        vec3 hairSpec = (white + hairColor * color) * orm.r * clamp(dot(L, N), 0.0, 1.0) * dlc;
        punctualSpec = mix(punctualSpec, hairSpec, hairBlend);
        punctualDiffuse = punctualSSS * hairColor + punctualDiffuse * (1.0 - hairColor);
    }
    vec3 diffuseColor = (isEye ? 1.0 : 1.0 - metal) * base;
    vec3 diffuse = isSkin ? (sssAmbient(N, curvature) * occ + punctualSSS) * diffuseColor
                 : isHair ? (punctualDiffuse + clamp(ambient(N) * occ, 0.0, 1.0) * occ) * diffuseColor
                 : (punctualDiffuse + ambDiffuse) * diffuseColor;
    float bust = smoothstep(u_Bust.x, u_Bust.y, v_Height);
    if (bust < 0.004) discard;
    o_Color = vec4(diffuse + rim + punctualSpec + ambSpec + glint, bust);
})glsl";
