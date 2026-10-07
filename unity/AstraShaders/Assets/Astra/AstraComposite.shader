// SPDX-License-Identifier: MIT
//
// Composites Astra's published picture over the game's finished frame, behind and in front of the
// game's own geometry. One full-screen triangle, no includes, no pipeline library: it runs the same
// in every pipeline, on every graphics API the bundle is built for.
//
// The picture was rendered for an EARLIER camera (the one its `cam` named). Each pixel of the
// camera presenting now is looked up in it by ROTATION (exact for a turning camera, which is what
// ghosts), and her point there is moved into the present camera with the full transform, so the
// depth test compares like with like. Two ways to test:
//   pass 0 "Composite": against the game's depth TEXTURE (Built-in, URP) — two-way, soft;
//   pass 1 "CompositeZ": by the GPU against the camera's depth BUFFER bound with the target — she
//     writes her own depth (SV_Depth). For a pipeline whose depth texture this shader cannot read
//     (HDRP's mip-pyramid atlas), from a pass that binds the camera's depth buffer.
Shader "Hidden/Astra/Composite"
{
    HLSLINCLUDE
    #pragma target 3.5

    // Her picture: RGBA8 premultiplied, display-encoded, uploaded RAW (rows top-down, so the
    // image's top is at v = 0); and her linear depth in metres, 0 where she is not.
    Texture2D _AstraColour;
    SamplerState sampler_AstraColour;
    Texture2D<float> _AstraDepth;
    // The game's depth (whatever its pipeline bound globally) and how to linearise it.
    Texture2D _CameraDepthTexture;
    SamplerState sampler_CameraDepthTexture;

    float4 _AstraSize;      // xy: picture size in texels, zw: 1/size
    float4 _AstraProjNow;   // the presenting camera's projection: x P00, y P11, z P02, w P12 (its rays)
    float4 _AstraTanThen;   // xy: the same for the camera the picture was rendered from
    // The rotation from the presenting camera's view (x right, y up, z forward) to the earlier
    // one's, as three rows; and the earlier eye in the presenting view.
    float4 _AstraRot0;
    float4 _AstraRot1;
    float4 _AstraRot2;
    float4 _AstraEyeThen;
    float4x4 _AstraGpuProj;    // the presenting camera's projection in the platform's depth range (y unflipped)
    float4x4 _AstraInvGpuProj; // its inverse: a depth texel at a pixel -> the view-space point there
    float4 _AstraZNdc;         // x, y: NDC z = texel * x + y (1, 0; OpenGL 2, -1)
    float4 _AstraTest;      // x: 1 = test against the game's depth texture, y: bias (m), z: softness (m)
    float4 _AstraOut;       // x: clip y sign for the target (0 = take _ProjectionParams'), y: 1 = write linear, z: opacity
    float4 _ProjectionParams; // set by Unity inside a Built-in camera's render: x = -1 when its target is flipped

    struct Varyings
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0; // 0..1, origin bottom-left
    };

    Varyings Vert(uint id : SV_VertexID)
    {
        Varyings o;
        float2 uv = float2((id << 1) & 2, id & 2);
        o.uv = uv;
        o.pos = float4(uv * 2 - 1, 0.5, 1);
        o.pos.y *= _AstraOut.x != 0 ? _AstraOut.x : _ProjectionParams.x;
        return o;
    }

    float3 SrgbToLinear(float3 c)
    {
        return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
    }

    // Her depth near `texel`: the nearest of the 2x2 she covers, so a pixel at her antialiased
    // edge — colour partly hers, centre texel empty — still has a depth.
    float NearestDepth(float2 texel)
    {
        int2 p = int2(floor(texel - 0.5));
        int2 hi = int2(_AstraSize.xy) - 1;
        float d = 1e9;
        [unroll] for (int k = 0; k < 4; k++)
        {
            int2 q = clamp(p + int2(k & 1, k >> 1), 0, hi);
            float s = _AstraDepth.Load(int3(q, 0));
            d = s > 0 ? min(d, s) : d;
        }
        return d;
    }

    // Her colour at this pixel of the presenting camera (premultiplied, in the target's encoding)
    // and her depth along the presenting view; discards where she is not.
    float4 Her(Varyings i, out float depthNow)
    {
        depthNow = 0;
        if (_AstraOut.z <= 0) discard;
        // The ray through this pixel, in the presenting camera's view, then in the earlier one's.
        float2 ndc = i.uv * 2 - 1;
        float3 rayNow = float3((ndc + _AstraProjNow.zw) / _AstraProjNow.xy, 1);
        float3 rayThen = float3(dot(_AstraRot0.xyz, rayNow), dot(_AstraRot1.xyz, rayNow), dot(_AstraRot2.xyz, rayNow));
        if (rayThen.z <= 1e-4) discard;
        float2 ndcThen = rayThen.xy / rayThen.z / _AstraTanThen.xy;
        float2 uvThen = ndcThen * 0.5 + 0.5;
        if (any(uvThen < 0) || any(uvThen > 1)) discard;
        float2 tex = float2(uvThen.x, 1 - uvThen.y);

        float4 c = _AstraColour.SampleLevel(sampler_AstraColour, tex, 0);
        if (c.a <= 0.002) discard;
        float depthThen = NearestDepth(tex * _AstraSize.xy);
        if (depthThen >= 1e8) discard;

        // Her point, from the earlier view into the presenting one: x = R^T y + eye.
        float3 pThen = float3(ndcThen * _AstraTanThen.xy, 1) * depthThen;
        float3 pNow = _AstraRot0.xyz * pThen.x + _AstraRot1.xyz * pThen.y + _AstraRot2.xyz * pThen.z + _AstraEyeThen.xyz;
        depthNow = pNow.z;

        if (_AstraOut.y > 0.5)
        {
            // Premultiplied in DISPLAY space: un-premultiply, decode, re-premultiply.
            float3 straight = c.rgb / max(c.a, 1e-4);
            c.rgb = SrgbToLinear(saturate(straight)) * c.a;
        }
        return c * _AstraOut.z;
    }

    float4 FragTexture(Varyings i) : SV_Target
    {
        float depthNow;
        float4 c = Her(i, depthNow);
        float visible = 1;
        if (_AstraTest.x > 0.5)
        {
            // The game's depth texel back to metres through the camera's own projection (an oblique
            // mirror projection included): the view-space point at this pixel, its distance ahead.
            float raw = _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, i.uv, 0).r;
            float4 v = mul(_AstraInvGpuProj, float4(i.uv * 2 - 1, raw * _AstraZNdc.x + _AstraZNdc.y, 1));
            float game = -v.z / v.w;
            visible = saturate((game + _AstraTest.y - depthNow) / max(_AstraTest.z, 1e-4));
        }
        return c * visible;
    }

    // The camera's depth buffer does the test: she writes the device depth of her point, pulled
    // toward the camera by the bias so her feet do not fight the ground.
    float4 FragDepthBuffer(Varyings i, out float depth : SV_Depth) : SV_Target
    {
        float depthNow;
        float4 c = Her(i, depthNow);
        float z = max(depthNow - _AstraTest.y, 1e-3);
        // Her point at that distance along this pixel's ray, through the camera's projection.
        float2 ndc = i.uv * 2 - 1;
        float3 p = float3((ndc + _AstraProjNow.zw) / _AstraProjNow.xy, 1) * z;
        float4 clip = mul(_AstraGpuProj, float4(p.xy, -p.z, 1));
        depth = saturate((clip.z / clip.w - _AstraZNdc.y) / _AstraZNdc.x);
        return c;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" "PreviewType" = "Plane" }
        Pass
        {
            Name "Composite"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTexture
            ENDHLSL
        }
        Pass
        {
            Name "CompositeZ"
            ZTest LEqual // Unity turns it into GEqual where the depth buffer is reversed
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepthBuffer
            ENDHLSL
        }
    }
    Fallback Off
}
