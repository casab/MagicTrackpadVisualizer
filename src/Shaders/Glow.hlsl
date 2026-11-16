// Glow: the pad seen from above with one soft spot per finger. The spot has the size, shape
// and angle of the contact the trackpad measured; pressure makes it brighter and widens the
// light around it. A force click sends a ring of light across the pad; holding it heats the
// spot from blue to white hot and makes it shake.

float4 GlowPS(float4 position : SV_Position) : SV_Target
{
    float2 uv = (position.xy - PadRect.xy) / PadRect.zw;
    float cover;
    float3 color = Plate(uv, cover);

    float2 p = PadPoint(uv);
    p += Tremor(p).xy * 2.5;   // the spots shake around a held force click
    float heat = Heat(p), flicker = FlickerEverywhere(), crest = Shock(p);

    for (int i = 0; i < MaxTouches; i++)
    {
        float pressure = Touch[i].z, presence = Touch[i].w;
        float3 tint = Shape[i].w > 0.5 ? float3(1.00, 0.62, 0.04) : float3(0.04, 0.52, 1.00);
        tint = lerp(tint, HotColor(heat), saturate(heat * 1.4) * flicker);

        // Into the contact's own axes; q is 1 at the edge of the contact.
        float s, c;
        sincos(Shape[i].z, s, c);
        float2 axes = Shape[i].xy * View.w * 1.3 + 0.018;
        float2 d = p - PadPoint(Touch[i].xy);
        float q = length(float2(d.x * c + d.y * s, -d.x * s + d.y * c) / axes);
        float2 below = d - float2(0, 0.014);
        float shadow = length(float2(below.x * c + below.y * s, -below.x * s + below.y * c) / (axes * 1.35));

        color *= 1 - 0.55 * presence * exp(-shadow * shadow * 1.2);                    // shadow on the glass
        float reach = 0.9 + 1.1 * pressure;
        color += tint * presence * (0.12 + 0.40 * pressure) * exp(-q * q / (2 * reach * reach));   // light around it
        float3 body = lerp(tint, float3(1, 1, 1), 0.15 + 0.55 * pressure * exp(-q * q * 2));
        color = lerp(color, body, presence * (0.55 + 0.45 * pressure) * smoothstep(1.2, 0.5, q));
    }

    color += float3(0.45, 0.20, 0.04) * heat * heat * flicker;          // the glow of the charge on the glass
    color += float3(0.30, 0.55, 1.00) * saturate(crest) * 0.42;         // the shock wave: a ring of light
    color *= 1 - 0.25 * saturate(-crest);                               // with a darker band behind it

    return float4(lerp(Background.rgb, color, cover), 1);
}
