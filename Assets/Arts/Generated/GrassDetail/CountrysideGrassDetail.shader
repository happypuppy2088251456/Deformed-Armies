Shader "DeformedArmies/Countryside Grass Detail"
{
    Properties
    {
        _MainTex ("Original terrain atlas", 2D) = "white" {}
        _Color ("Terrain tint", Color) = (1,1,1,1)
        _GrassTex ("Grass detail", 2D) = "white" {}
        _GrassTileSize ("Grass tile size (metres)", Range(0.5, 5)) = 1.5
        _GrassStrength ("Grass detail strength", Range(0, 1)) = 0.9
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex;
        sampler2D _GrassTex;
        fixed4 _Color;
        float _GrassTileSize;
        half _GrassStrength;
        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
            float3 worldNormal;
        };
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed3 atlas = tex2D(_MainTex, IN.uv_MainTex).rgb;
            // Keep the atlas roads, dirt and rocks. Only green, upward-facing areas receive grass.
            half greenOverRed = (atlas.g - atlas.r) / max(atlas.g, 0.001);
            half greenOverBlue = (atlas.g - atlas.b) / max(atlas.g, 0.001);
            half grassMask = smoothstep(0.01, 0.10, greenOverRed)
                           * smoothstep(0.12, 0.35, greenOverBlue)
                           * smoothstep(0.35, 0.75, normalize(IN.worldNormal).y);
            // World-space scale keeps blades small regardless of the map's atlas UVs.
            fixed3 detail = tex2D(_GrassTex, IN.worldPos.xz / max(_GrassTileSize, 0.01)).rgb;
            half macroLuma = dot(atlas, half3(0.2126, 0.7152, 0.0722));
            detail *= lerp(1.0, clamp(macroLuma / 0.16, 0.7, 1.3), 0.35);
            half nearDetail = 1.0 - smoothstep(25.0, 65.0, distance(_WorldSpaceCameraPos, IN.worldPos));
            o.Albedo = lerp(atlas, detail, grassMask * _GrassStrength * nearDetail) * _Color.rgb;
            o.Metallic = 0;
            o.Smoothness = 0;
            o.Occlusion = 1;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
