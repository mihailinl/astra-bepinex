// SPDX-License-Identifier: MIT
//
// Composites Astra's published picture over the game's finished frame, behind and in front of the
// game's own geometry. One full-screen triangle, no includes, no pipeline library: it runs the same
// in the Built-in pipeline and URP, on every graphics API the bundle is built for. (HDRP keeps its
// depth as a mip-pyramid atlas array this pass does not read: there it is drawn without the test.)
//
// The picture was rendered for an EARLIER camera (the one its `cam` named). Each pixel of the
// camera presenting now is looked up in it by ROTATION (exact for a turning camera, which is what
// ghosts), and her point there is moved into the present camera with the full transform, so the
// depth test compares like with like.
Shader "Hidden/Astra/Composite"
{
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
            #pragma fragment Frag
            #pragma target 3.5

            // Her picture: RGBA8 premultiplied, display-encoded, uploaded RAW (rows top-down, so
            // the image's top is at v = 0); and her linear depth in metres, 0 where she is not.
            Texture2D _AstraColour;
            SamplerState sampler_AstraColour;
            Texture2D<float> _AstraDepth;
            // The game's depth (whatever its pipeline bound globally) and how to linearise it.
            Texture2D _CameraDepthTexture;
            SamplerState sampler_CameraDepthTexture;

            float4 _AstraSize;      // xy: picture size in texels, zw: 1/size
            float4 _AstraTanNow;    // xy: tan of the half-fov (x, y) of the camera presenting
            float4 _AstraTanThen;   // xy: the same for the camera the picture was rendered from
            // The rotation from the presenting camera's view (x right, y up, z forward) to the
            // earlier one's, as three rows; and the earlier eye in the presenting view.
            float4 _AstraRot0;
            float4 _AstraRot1;
            float4 _AstraRot2;
            float4 _AstraEyeThen;
            float4 _AstraZParams;   // linear eye depth = 1 / (z * raw + w), for the game's depth
            float4 _AstraTest;      // x: 1 = test against the game's depth, y: bias (m), z: softness (m)
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

            // Her depth near `texel`: the nearest of the 2×2 she covers, so a pixel at her
            // antialiased edge — colour partly hers, centre texel empty — still has a depth.
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

            float4 Frag(Varyings i) : SV_Target
            {
                if (_AstraOut.z <= 0) discard;
                // The ray through this pixel, in the presenting camera's view, then in the earlier one's.
                float2 ndc = i.uv * 2 - 1;
                float3 rayNow = float3(ndc * _AstraTanNow.xy, 1);
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

                float visible = 1;
                if (_AstraTest.x > 0.5)
                {
                    float raw = _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, i.uv, 0).r;
                    float game = 1.0 / (_AstraZParams.z * raw + _AstraZParams.w);
                    visible = saturate((game + _AstraTest.y - pNow.z) / max(_AstraTest.z, 1e-4));
                }

                if (_AstraOut.y > 0.5)
                {
                    // Premultiplied in DISPLAY space: un-premultiply, decode, re-premultiply.
                    float3 straight = c.rgb / max(c.a, 1e-4);
                    c.rgb = SrgbToLinear(saturate(straight)) * c.a;
                }
                return c * (visible * _AstraOut.z);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
