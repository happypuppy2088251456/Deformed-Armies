Shader "DeformedArmies/Leafy Grass"
{
    Properties
    {
        _MainTex ("Leafy grass colour", 2D) = "white" {}
        [Normal] _BumpMap ("Leafy grass normal", 2D) = "bump" {}
        _Roughness ("Leafy grass roughness", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _TileSize ("Tile width in metres", Range(0.5, 8)) = 2
        _BumpScale ("Normal strength", Range(0, 2)) = 0.8
        _SmoothnessScale ("Smoothness amount", Range(0, 1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        #include "UnityStandardUtils.cginc"
        sampler2D _MainTex, _BumpMap, _Roughness;
        fixed4 _Color;
        float _TileSize;
        half _BumpScale;
        half _SmoothnessScale;
        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
            INTERNAL_DATA
        };
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Physical scale is independent of the imported map's stretched UVs.
            float2 uv = IN.worldPos.xz / max(_TileSize, 0.01);
            o.Albedo = tex2D(_MainTex, uv).rgb * _Color.rgb;
            float3 n = normalize(WorldNormalVector(IN, float3(0,0,1)));
            float3 tangent = float3(1,0,0) - n * n.x;
            tangent = normalize(tangent + float3(0,0,0.0001));
            float3 bitangent = normalize(cross(tangent, n));
            half3 detail = UnpackScaleNormal(tex2D(_BumpMap, uv), _BumpScale);
            float3 worldDetail = normalize(tangent * detail.x + bitangent * detail.y + n * detail.z);
            // Convert the world-aligned detail to the mesh's tangent space.
            o.Normal = normalize(float3(
                dot(worldDetail, WorldNormalVector(IN, float3(1,0,0))),
                dot(worldDetail, WorldNormalVector(IN, float3(0,1,0))),
                dot(worldDetail, n)));
            o.Metallic = 0;
            o.Smoothness = (1 - tex2D(_Roughness, uv).r) * _SmoothnessScale;
            o.Occlusion = 1;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
