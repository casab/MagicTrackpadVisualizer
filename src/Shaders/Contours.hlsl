// Contours: the pad seen from above, with pressure as a height map. Every finger raises a
// hill, higher and wider the harder it presses, drawn with contour lines like a map.
// A force click sends a ridge running outwards across the map; holding it piles the hill
// higher, heats it and makes the lines around the finger shake.

float4 ContoursPS(float4 position : SV_Position) : SV_Target
{
    float2 uv = (position.xy - PadRect.xy) / PadRect.zw;
    float cover;
    float3 color = Plate(uv, cover);

    float2 p = PadPoint(uv);
    p += Tremor(p).xy * 2.5;   // the map itself shakes around a held force click
    float heat = Heat(p), flicker = Flicker(p), crest = Shock(p);

    float field = 0;
    for (int i = 0; i < MaxTouches; i++)
    {
        float pressure = Touch[i].z, presence = Touch[i].w;
        float spread = 0.05 + 1.2 * max(Shape[i].x, Shape[i].y) * View.w + 0.10 * pressure;
        float2 d = p - PadPoint(Touch[i].xy);
        field += presence * (0.12 + 0.88 * pressure) * exp(-dot(d, d) / (2 * spread * spread));
    }
    field += 0.30 * heat * flicker;         // the charge piles the hill higher
    field = max(field + 0.20 * crest, 0);   // the shock wave is a ridge with a ditch behind it

    // Height colours: deep blue at the foot of a hill to white at a hard press, and from
    // orange to white hot where a force click is held.
    float3 ramp = lerp(float3(0.05, 0.13, 0.33), float3(0.04, 0.52, 1.00), saturate(field * 2.2));
    ramp = lerp(ramp, float3(0.45, 0.95, 1.00), saturate(field * 2.2 - 0.9));
    ramp = lerp(ramp, float3(1, 1, 1), saturate(field * 2.2 - 1.7));
    ramp = lerp(ramp, HotColor(heat), saturate(heat * 1.3) * flicker);
    color = lerp(color, ramp * 0.6, smoothstep(0.015, 0.16, field));

    // One contour line per step of height, about a pixel wide.
    float level = field * 14;
    float onLine = 1 - saturate(abs(frac(level - 0.5) - 0.5) / max(fwidth(level), 1e-6) - 0.35);
    color = lerp(color, ramp + 0.25, onLine * smoothstep(0.03, 0.08, field) * 0.9);
    color += float3(0.45, 0.70, 1.00) * saturate(crest) * 0.30;   // the crest of the wave catches the light

    return float4(lerp(Background.rgb, color, cover), 1);
}
