// Shared by every view: the data the app sends each frame, and a few helpers.
// The layout of Frame must match what GpuSurface.cs writes.

static const int MaxTouches = 8;

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProjection;   // camera of the 3D views
    float4 Touch[MaxTouches];   // xy: position on the pad, 0..1 with y down; z: pressure 0..1; w: presence 0..1
                                // (1 touching, about 0.15 hovering, fading to 0 after the finger lifts)
    float4 Shape[MaxTouches];   // xy: contact semi-axes as fractions of the pad's width (x across the finger,
                                // y along it); z: angle in radians, clockwise on screen; w: 1 = palm
    float4 Wave[MaxTouches];    // the shock wave this finger's force click set off: xy: where it started (0..1);
                                // z: its age in seconds; w: strength 1..0
    float4 Force[MaxTouches];   // x: this finger's charge, 0..1 the longer it holds a force click
    float4 View;                // xy: size of the surface in pixels; z: time in seconds; w: pad width / height
    float4 State;               // x: button down 0..1; y: any force click 0..1; z: the strongest charge 0..1
    float4 Background;          // colour of the window around the surface
    float4 PadRect;             // where the flat views draw the pad, in pixels: x, y, width, height
};

// Pad units: x runs 0..aspect and y 0..1, so a distance means the same in both directions.
float2 PadPoint(float2 uv)
{
    return float2(uv.x * View.w, uv.y);
}

// One triangle that covers the whole surface, for the views that are drawn per pixel.
float4 FullscreenVS(uint id : SV_VertexID) : SV_Position
{
    float2 corner = float2((id << 1) & 2, id & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
}

// The flat views draw the pad as a rounded plate. Returns the plate's colour and, in
// `cover`, how much of the pixel lies on it.
float3 Plate(float2 uv, out float cover)
{
    float2 p = PadPoint(uv), halfSize = float2(View.w, 1) * 0.5;
    float2 q = abs(p - halfSize) - (halfSize - 0.05);
    float distance = (length(max(q, 0)) + min(max(q.x, q.y), 0) - 0.05) * PadRect.w;   // pixels, negative inside
    cover = saturate(0.5 - distance);

    float3 color = lerp(float3(0.20, 0.22, 0.27), float3(0.13, 0.15, 0.19), uv.y);
    color += float3(0.02, 0.06, 0.14) * State.x + float3(0.03, 0.09, 0.20) * State.y;
    float3 rim = lerp(float3(0.31, 0.34, 0.40), float3(0.80, 0.90, 1.00), max(State.x * 0.6, State.y));
    return lerp(color, rim, saturate(1.5 - abs(distance + 1.5)));
}

// ---- Force click, the same in every view ---------------------------------------------
// Every finger has its own: engaging it sets off a shock wave from that finger, and holding
// it builds up a charge that makes things around that finger shake and heat up.

static const float WaveSpeed = 1.3;   // pad heights per second

// The shock waves of all fingers. Each is a tall crest, a trough behind it and a smaller
// second crest after that, 1 at the top of the first crest; it weakens as it spreads and ages.
float Shock(float2 p)
{
    float shock = 0;
    for (int i = 0; i < MaxTouches; i++)
    {
        float travelled = Wave[i].z * WaveSpeed;
        float x = (length(p - PadPoint(Wave[i].xy)) - travelled) / 0.08;
        float crest = exp(-x * x) - 0.65 * exp(-(x + 1.7) * (x + 1.7)) + 0.35 * exp(-(x + 3.4) * (x + 3.4));
        shock += sqrt(Wave[i].w) * crest / (1 + 0.7 * travelled);
    }
    return shock;
}

// How strongly one finger's held force click acts at a point: 1 at the finger when fully
// charged. It reaches farther the more it is charged.
float HeatOf(int i, float2 p)
{
    float charge = Force[i].x, reach = 0.10 + 0.42 * charge;
    float2 d = p - PadPoint(Touch[i].xy);
    return charge * exp(-dot(d, d) / (2 * reach * reach));
}

// The same for all fingers together.
float Heat(float2 p)
{
    float heat = 0;
    for (int i = 0; i < MaxTouches; i++) heat += HeatOf(i, p);
    return saturate(heat);
}

// The shaking around every finger that holds a force click: fast, uneven, in all three
// directions (the flat views use only x and y). Its strength and its speed both rise with
// that finger's charge, and each finger shakes to its own rhythm.
float3 Tremor(float2 p)
{
    float t = View.z;
    float3 shake = 0;
    for (int i = 0; i < MaxTouches; i++)
    {
        float charge = Force[i].x, speed = 45 + 70 * charge, own = i * 1.7;
        float a = sin(t * speed + p.x * 43 + own) * sin(t * speed * 1.37 + p.y * 37 + own);
        float b = sin(t * speed * 1.83 + (p.x - p.y) * 59 + own);
        float c = sin(t * speed * 2.31 + (p.x + p.y) * 51 + own);
        shake += float3(a * 0.004, b * 0.004, (a + 0.6 * c) * 0.022) * HeatOf(i, p) * charge;
    }
    float strongest = State.z;
    shake.z += sin(t * (45 + 70 * strongest) * 0.9) * 0.004 * strongest * strongest * strongest;   // everything starts to hum
    return shake;
}

// The light of the charge flickers, faster as it builds. The first varies from place to
// place; the second is the same everywhere, for soft glows that would show that pattern.
float Flicker(float2 p)
{
    return 0.75 + 0.25 * sin(View.z * (30 + 90 * State.z) + p.x * 17 + p.y * 13);
}

float FlickerEverywhere()
{
    return 0.75 + 0.25 * sin(View.z * (30 + 90 * State.z));
}

// From orange to white hot.
float3 HotColor(float heat)
{
    return lerp(float3(1.00, 0.55, 0.15), float3(1.00, 0.97, 0.85), saturate(heat * 1.6 - 0.5));
}
