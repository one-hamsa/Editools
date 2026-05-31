Shader "Hidden/Editools/TriggerDebug"
{
	Properties { }

	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
		LOD 100

		// ---- Forward pass: gray scene, green where inside a selected collider ----
		Pass
		{
			Name "TriggerDebugForward"
			Tags { "LightMode"="UniversalForward" }

			Cull Back
			ZWrite On
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_instancing

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			// Max collider volumes uploaded by TriggerDebug.cs. Must match k_MaxVolumes there.
			#define TRIG_MAX 128

			// Box volumes: each matrix maps a world position into unit-cube space,
			// where the box interior is |xyz| <= 0.5 on every axis.
			float4x4 _TrigBoxMatrices[TRIG_MAX];
			int      _TrigBoxCount;

			// Sphere volumes: xyz = world center, w = world radius.
			float4 _TrigSpheres[TRIG_MAX];
			int    _TrigSphereCount;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS   : NORMAL;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float3 normalWS   : TEXCOORD1;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};

			Varyings vert(Attributes input)
			{
				Varyings output;
				UNITY_SETUP_INSTANCE_ID(input);
				UNITY_TRANSFER_INSTANCE_ID(input, output);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

				VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = vpi.positionCS;
				output.positionWS = vpi.positionWS;
				output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
				return output;
			}

			bool InsideAnyVolume(float3 worldPos)
			{
				for (int b = 0; b < _TrigBoxCount; b++)
				{
					float4 q = mul(_TrigBoxMatrices[b], float4(worldPos, 1.0));
					float3 a = abs(q.xyz);
					if (max(max(a.x, a.y), a.z) <= 0.5)
						return true;
				}

				for (int s = 0; s < _TrigSphereCount; s++)
				{
					float4 sph = _TrigSpheres[s];
					if (distance(worldPos, sph.xyz) <= sph.w)
						return true;
				}

				return false;
			}

			float4 frag(Varyings input) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(input);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

				float3 gray  = float3(0.22, 0.22, 0.22);
				float3 green = float3(0.10, 0.85, 0.20);

				float3 baseColor = InsideAnyVolume(input.positionWS) ? green : gray;

				// Simple directional shading so surface form stays readable.
				float3 n = normalize(input.normalWS);
				float shade = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.3, 1.0, 0.25))));

				return float4(baseColor * shade, 1.0);
			}
			ENDHLSL
		}

		// ---- DepthOnly pass (for URP depth pre-pass) ----
		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode"="DepthOnly" }

			ZWrite On
			ColorMask 0

			HLSLPROGRAM
			#pragma vertex vertDepth
			#pragma fragment fragDepth
			#pragma multi_compile_instancing

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes
			{
				float4 positionOS : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};

			Varyings vertDepth(Attributes input)
			{
				Varyings output;
				UNITY_SETUP_INSTANCE_ID(input);
				UNITY_TRANSFER_INSTANCE_ID(input, output);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				return output;
			}

			half4 fragDepth(Varyings input) : SV_Target
			{
				return 0;
			}
			ENDHLSL
		}
	}
	FallBack Off
}
