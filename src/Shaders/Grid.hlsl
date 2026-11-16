// Grid: the pad is a sheet seen at an angle. Every finger presses a dent into it, deeper
// and wider the harder it presses, and the grid drawn on the sheet bends with it.
// A force click sets off a shock wave that lifts the sheet as it runs outwards; holding it
// makes the sheet around the finger shake, harder, wider and hotter the longer it is held.

struct GridVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float height : TEXCOORD1;
};

// How far the sheet is pushed down at a point, in pad units.
float Dent(float2 p)
{
    float dent = 0;
    for (int i = 0; i < MaxTouches; i++)
    {
        float pressure = Touch[i].z, presence = Touch[i].w;
        float size = max(Shape[i].x, Shape[i].y) * View.w;       // contact radius in pad units
        float spread = 0.045 + 1.1 * size + 0.07 * pressure;
        // A resting finger reads about 0.02..0.15 and a hard press 1: the power keeps light
        // touches visible without flattening the difference to a hard one.
        float depth = presence * (0.018 + 0.34 * pow(pressure, 0.6));
        float2 d = p - PadPoint(Touch[i].xy);
        dent += depth * exp(-dot(d, d) / (2 * spread * spread));
    }
    return dent;
}

GridVertex GridVS(float2 uv : POSITION)
{
    GridVertex o;
    float2 p = PadPoint(uv);
    float3 shake = Tremor(p);
    o.height = -Dent(p) + 0.21 * Shock(p) + shake.z;
    o.uv = uv;
    // The sheet lies in the XY plane with its far edge (the top of the pad) at +Y; Z is up.
    o.position = mul(float4(p.x - View.w * 0.5 + shake.x, 0.5 - p.y + shake.y, o.height, 1), ViewProjection);
    return o;
}

// 1 on a grid line and 0 away from it. The line is about `width` pixels wide wherever it
// is on screen, however the perspective and the dents stretch the sheet.
float GridLine(float2 g, float width)
{
    float2 d = abs(frac(g - 0.5) - 0.5) / max(fwidth(g), 1e-6);
    return 1 - saturate(min(d.x, d.y) - width);
}

float4 GridPS(GridVertex i) : SV_Target
{
    float2 p = PadPoint(i.uv);
    float depth = saturate(-i.height / 0.30);

    float fine = GridLine(p * 28, 0.15);
    float bold = GridLine(p * 7, 0.75);

    // Where the fingers are: a glow on the sheet under each one.
    float touch = 0;
    for (int k = 0; k < MaxTouches; k++)
    {
        float radius = 0.03 + max(Shape[k].x, Shape[k].y) * View.w;
        float2 d = p - PadPoint(Touch[k].xy);
        touch += Touch[k].w * (0.35 + 0.65 * Touch[k].z) * exp(-dot(d, d) / (2 * radius * radius));
    }
    touch = saturate(touch);

    // The held force click heats the sheet around the finger: blue to orange to white hot,
    // flickering faster as the charge builds.
    float heat = Heat(p);
    float flicker = Flicker(p);
    float3 hot = HotColor(heat);

    float crest = saturate(Shock(p));

    float3 calm = float3(0.20, 0.42, 0.85), deep = float3(0.45, 0.92, 1.00);
    float3 lineColor = lerp(calm, deep, saturate(depth * 1.4 + touch * 0.5));
    lineColor = lerp(lineColor, hot, saturate(heat * 1.3) * flicker);
    lineColor = lerp(lineColor, float3(0.85, 0.96, 1.00), crest);
    float lines = saturate(0.42 * fine + 0.75 * bold) * (0.55 + 1.1 * depth + 0.5 * touch + 1.2 * heat * flicker + 2.2 * crest);

    float3 color = Background.rgb;
    color += float3(0.02, 0.10, 0.26) * (depth * 0.9 + touch * 0.8);    // light pooling in the dents
    color += float3(0.03, 0.08, 0.16) * State.x;                        // button down
    color += float3(0.40, 0.17, 0.03) * heat * heat * flicker;          // the glow of the charge
    color += float3(0.10, 0.22, 0.40) * crest;                          // the wave itself is faintly lit
    color += lineColor * lines * (1 + 0.4 * State.y);

    // The sheet fades out at its rim, and its far rows are a little dimmer.
    float2 rim = min(i.uv, 1 - i.uv) * float2(View.w, 1);
    float fade = smoothstep(0, 0.05, min(rim.x, rim.y)) * lerp(0.6, 1, i.uv.y);
    return float4(lerp(Background.rgb, color, fade), 1);
}
