using UnityEngine;
using System.Collections;


[ExecuteInEditMode] // Make water live-update even when not in play mode
public class Water : MonoBehaviour
{
	public enum WaterMode {
		Simple = 0,
		Reflective = 1,
		Refractive = 2,
	};
	public WaterMode m_WaterMode = WaterMode.Refractive;
	public bool m_DisablePixelLights = true;
	public int m_TextureSize = 256;
	public float m_ClipPlaneOffset = 0.07f;
	
	public LayerMask m_ReflectLayers = -1;
	public LayerMask m_RefractLayers = -1;
		
	private Hashtable m_ReflectionCameras = new Hashtable(); // Camera -> Camera table
	private Hashtable m_RefractionCameras = new Hashtable(); // Camera -> Camera table
	
	private RenderTexture m_ReflectionTexture = null;
	private RenderTexture m_RefractionTexture = null;
	private WaterMode m_HardwareWaterSupport = WaterMode.Refractive;
	private int m_OldReflectionTextureSize = 0;
	private int m_OldRefractionTextureSize = 0;
	
	private static bool s_InsideWater = false;

	// This is called when it's known that the object will be rendered by some
	// camera. We render reflections / refractions and do other updates here.
	// Because the script executes in edit mode, reflections for the scene view
	// camera will just work!
	public void OnWillRenderObject()
	{
		if( !enabled || !renderer || !renderer.sharedMaterial || !renderer.enabled )
			return;
			
		Camera cam = Camera.current;
		if( !cam )
			return;
	
		// Safeguard from recursive water reflections.		
		if( s_InsideWater )
			return;
		s_InsideWater = true;
		
		// Actual water rendering mode depends on both the current setting AND
		// the hardware support. There's no point in rendering refraction textures
		// if they won't be visible in the end.
		m_HardwareWaterSupport = FindHardwareWaterSupport();
		WaterMode mode = GetWaterMode();
		
		Camera reflectionCamera, refractionCamera;
		CreateWaterObjects( cam, out reflectionCamera, out refractionCamera );
		
		// find out the reflection plane: position and normal in world space
		Vector3 pos = transform.position;
		Vector3 normal = transform.up;
		
		// Optionally disable pixel lights for reflection/refraction
		int oldPixelLightCount = QualitySettings.pixelLightCount;
		if( m_DisablePixelLights )
			QualitySettings.pixelLightCount = 0;
		
		UpdateCameraModes( cam, reflectionCamera );
		UpdateCameraModes( cam, refractionCamera );
		
		// Render reflection if needed
		if( mode >= WaterMode.Reflective )
		{
			// Reflect camera around reflection plane
			float d = -Vector3.Dot (normal, pos) - m_ClipPlaneOffset;
			Vector4 reflectionPlane = new Vector4 (normal.x, normal.y, normal.z, d);
		
			Matrix4x4 reflection = Matrix4x4.zero;
			CalculateReflectionMatrix (ref reflection, reflectionPlane);
			Vector3 oldpos = cam.transform.position;
			Vector3 newpos = reflection.MultiplyPoint( oldpos );
			reflectionCamera.worldToCameraMatrix = cam.worldToCameraMatrix * reflection;
		
			// Setup oblique projection matrix so that near plane is our reflection
			// plane. This way we clip everything below/above it for free.
			Vector4 clipPlane = CameraSpacePlane( reflectionCamera, pos, normal, 1.0f );
			Matrix4x4 projection = cam.projectionMatrix;
			CalculateObliqueMatrix (ref projection, clipPlane);
			reflectionCamera.projectionMatrix = projection;
			
			reflectionCamera.cullingMask = ~(1<<4) & m_ReflectLayers.value; // never render water layer
			reflectionCamera.targetTexture = m_ReflectionTexture;
			GL.SetRevertBackfacing (true);
			reflectionCamera.transform.position = newpos;
			Vector3 euler = cam.transform.eulerAngles;
			reflectionCamera.transform.eulerAngles = new Vector3(0, euler.y, euler.z);
			reflectionCamera.Render();
			reflectionCamera.transform.position = oldpos;
			GL.SetRevertBackfacing (false);
			renderer.sharedMaterial.SetTexture( "_ReflectionTex", m_ReflectionTexture );
		}
		
		// Render refraction
		if( mode >= WaterMode.Refractive )
		{
			refractionCamera.worldToCameraMatrix = cam.worldToCameraMatrix;
		
			// Setup oblique projection matrix so that near plane is our reflection
			// plane. This way we clip everything below/above it for free.
			Vector4 clipPlane = CameraSpacePlane( refractionCamera, pos, normal, -1.0f );
			Matrix4x4 projection = cam.projectionMatrix;
			CalculateObliqueMatrix (ref projection, clipPlane);
			refractionCamera.projectionMatrix = projection;
			
			refractionCamera.cullingMask = ~(1<<4) & m_RefractLayers.value; // never render water layer
			refractionCamera.targetTexture = m_RefractionTexture;
			refractionCamera.transform.position = cam.transform.position;
			refractionCamera.transform.rotation = cam.transform.rotation;
			refractionCamera.Render();
			renderer.sharedMaterial.SetTexture( "_RefractionTex", m_RefractionTexture );
		}
		
		// Restore pixel light count
		if( m_DisablePixelLights )
			QualitySettings.pixelLightCount = oldPixelLightCount;
		
		// Setup shader keywords based on water mode
		switch( mode )
		{
		case WaterMode.Simple:
			Shader.EnableKeyword( "WATER_SIMPLE" );
			Shader.DisableKeyword( "WATER_REFLECTIVE" );
			Shader.DisableKeyword( "WATER_REFRACTIVE" );
			break;
		case WaterMode.Reflective:
			Shader.DisableKeyword( "WATER_SIMPLE" );
			Shader.EnableKeyword( "WATER_REFLECTIVE" );
			Shader.DisableKeyword( "WATER_REFRACTIVE" );
			break;
		case WaterMode.Refractive:
			Shader.DisableKeyword( "WATER_SIMPLE" );
			Shader.DisableKeyword( "WATER_REFLECTIVE" );
			Shader.EnableKeyword( "WATER_REFRACTIVE" );
			break;
		}
			
		s_InsideWater = false;
	}
	
	
	// Cleanup all the objects we possibly have created
	void OnDisable()
	{
		if( m_ReflectionTexture ) {
			DestroyImmediate( m_ReflectionTexture );
			m_ReflectionTexture = null;
		}
		if( m_RefractionTexture ) {
			DestroyImmediate( m_RefractionTexture );
			m_RefractionTexture = null;
		}
		foreach( DictionaryEntry kvp in m_ReflectionCameras )
        	DestroyImmediate( ((Camera)kvp.Value).gameObject );
        m_ReflectionCameras.Clear();
		foreach( DictionaryEntry kvp in m_RefractionCameras )
        	DestroyImmediate( ((Camera)kvp.Value).gameObject );
        m_RefractionCameras.Clear();
	}
	
	
	// This just sets up some matrices in the material; for really
	// old cards to make water texture scroll.
	void Update()
	{
		if( !renderer )
			return;
		Material mat = renderer.sharedMaterial;
		if( !mat )
			return;
			
		Vector4 waveSpeed = mat.GetVector( "WaveSpeed" );
		float waveScale = mat.GetFloat( "_WaveScale" );
		float t = Time.time / 40.0f;
		
		Vector3 offset = new Vector3( t * waveSpeed.x, t * waveSpeed.y, 0 );
		Vector3 scale = new Vector3( 1.0f/waveScale, 1.0f/waveScale, 1 );
		Matrix4x4 scrollMatrix = Matrix4x4.TRS( offset, Quaternion.identity, scale );
		mat.SetMatrix( "_WaveMatrix", scrollMatrix );
		
		offset = new Vector3( t * waveSpeed.z, t * waveSpeed.w, 0 );
		scrollMatrix = Matrix4x4.TRS( offset, Quaternion.identity, scale * 0.45f );
		mat.SetMatrix( "_WaveMatrix2", scrollMatrix );
	}
	
	private void UpdateCameraModes( Camera src, Camera dest )
	{
		if( dest == null )
			return;
		// set water camera to clear the same way as current camera
		dest.clearFlags = src.clearFlags;
		dest.backgroundColor = src.backgroundColor;		
		if( src.clearFlags == CameraClearFlags.Skybox )
		{
			Skybox sky = src.GetComponent(typeof(Skybox)) as Skybox;
			Skybox mysky = dest.GetComponent(typeof(Skybox)) as Skybox;
			if( !sky || !sky.material )
			{
				mysky.enabled = false;
			}
			else
			{
				mysky.enabled = true;
				mysky.material = sky.material;
			}
		}
		// update other values to match current camera.
		// even if we are supplying custom camera&projection matrices,
		// some of values are used elsewhere (e.g. skybox uses far plane)
		dest.farClipPlane = src.farClipPlane;
		dest.nearClipPlane = src.nearClipPlane;
		dest.orthographic = src.orthographic;
		dest.fieldOfView = src.fieldOfView;
		dest.aspect = src.aspect;
		dest.orthographicSize = src.orthographicSize;
	}
	
	// On-demand create any objects we need for water
	private void CreateWaterObjects( Camera currentCamera, out Camera reflectionCamera, out Camera refractionCamera )
	{
		WaterMode mode = GetWaterMode();
		
		reflectionCamera = null;
		refractionCamera = null;
		
		if( mode >= WaterMode.Reflective )
		{
			// Reflection render texture
			if( !m_ReflectionTexture || m_OldReflectionTextureSize != m_TextureSize )
			{
				if( m_ReflectionTexture )
					DestroyImmediate( m_ReflectionTexture );
				m_ReflectionTexture = new RenderTexture( m_TextureSize, m_TextureSize, 16 );
				m_ReflectionTexture.name = "__WaterReflection" + GetInstanceID();
				m_ReflectionTexture.isPowerOfTwo = true;
				m_ReflectionTexture.hideFlags = HideFlags.DontSave;
				m_OldReflectionTextureSize = m_TextureSize;
			}
			
			// Camera for reflection
			reflectionCamera = m_ReflectionCameras[currentCamera] as Camera;
			if( !reflectionCamera ) // catch both not-in-dictionary and in-dictionary-but-deleted-GO
			{
				GameObject go = new GameObject( "Water Refl Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(), typeof(Camera), typeof(Skybox) );
				reflectionCamera = go.camera;
				reflectionCamera.enabled = false;
				reflectionCamera.transform.position = transform.position;
				reflectionCamera.transform.rotation = transform.rotation;
				reflectionCamera.gameObject.AddComponent("FlareLayer");
				go.hideFlags = HideFlags.HideAndDontSave;
				m_ReflectionCameras[currentCamera] = reflectionCamera;
			}
		}
		
		if( mode >= WaterMode.Refractive )
		{
			// Refraction render texture
			if( !m_RefractionTexture || m_OldRefractionTextureSize != m_TextureSize )
			{
				if( m_RefractionTexture )
					DestroyImmediate( m_RefractionTexture );
				m_RefractionTexture = new RenderTexture( m_TextureSize, m_TextureSize, 16 );
				m_RefractionTexture.name = "__WaterRefraction" + GetInstanceID();
				m_RefractionTexture.isPowerOfTwo = true;
				m_RefractionTexture.hideFlags = HideFlags.DontSave;
				m_OldRefractionTextureSize = m_TextureSize;
			}
			
			// Camera for refraction
			refractionCamera = m_RefractionCameras[currentCamera] as Camera;
			if( !refractionCamera ) // catch both not-in-dictionary and in-dictionary-but-deleted-GO
			{
				GameObject go = new GameObject( "Water Refr Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(), typeof(Camera), typeof(Skybox) );
				refractionCamera = go.camera;
				refractionCamera.enabled = false;
				refractionCamera.transform.position = transform.position;
				refractionCamera.transform.rotation = transform.rotation;
				refractionCamera.gameObject.AddComponent("FlareLayer");
				go.hideFlags = HideFlags.HideAndDontSave;
				m_RefractionCameras[currentCamera] = refractionCamera;
			}
		}
	}
	
	private WaterMode GetWaterMode()
	{
		if( m_HardwareWaterSupport < m_WaterMode )
			return m_HardwareWaterSupport;
		else
			return m_WaterMode;
	}
	
	private WaterMode FindHardwareWaterSupport()
	{
		if( !SystemInfo.supportsRenderTextures || !renderer )
			return WaterMode.Simple;
			
		Material mat = renderer.sharedMaterial;
		if( !mat )
			return WaterMode.Simple;
			
		string mode = mat.GetTag("WATERMODE", false);
		if( mode == "Refractive" )
			return WaterMode.Refractive;
		if( mode == "Reflective" )
			return WaterMode.Reflective;
			
		return WaterMode.Simple;
	}
	
	// Extended sign: returns -1, 0 or 1 based on sign of a
	private static float sgn(float a)
	{
        if (a > 0.0f) return 1.0f;
        if (a < 0.0f) return -1.0f;
        return 0.0f;
	}
	
	// Given position/normal of the plane, calculates plane in camera space.
	private Vector4 CameraSpacePlane (Camera cam, Vector3 pos, Vector3 normal, float sideSign)
	{
		Vector3 offsetPos = pos + normal * m_ClipPlaneOffset;
		Matrix4x4 m = cam.worldToCameraMatrix;
		Vector3 cpos = m.MultiplyPoint( offsetPos );
		Vector3 cnormal = m.MultiplyVector( normal ).normalized * sideSign;
		return new Vector4( cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos,cnormal) );
	}
	
	// Adjusts the given projection matrix so that near plane is the given clipPlane
	// clipPlane is given in camera space. See article in Game Programming Gems 5.
	private static void CalculateObliqueMatrix (ref Matrix4x4 projection, Vector4 clipPlane)
	{
		Vector4 q;  
        q.x = (sgn(clipPlane.x) + projection[8]) / projection[0];
        q.y = (sgn(clipPlane.y) + projection[9]) / projection[5];
        q.z = -1.0F;
        q.w = (1.0F + projection[10]) / projection[14];
        
        Vector4 c = clipPlane * (2.0F / (Vector4.Dot (clipPlane, q)));
        
        projection[2] = c.x;
        projection[6] = c.y;
        projection[10] = c.z + 1.0F;
        projection[14] = c.w;
	}

	// Calculates reflection matrix around the given plane
	private static void CalculateReflectionMatrix (ref Matrix4x4 reflectionMat, Vector4 plane)
	{
	    reflectionMat.m00 = (1F - 2F*plane[0]*plane[0]);
	    reflectionMat.m01 = (   - 2F*plane[0]*plane[1]);
	    reflectionMat.m02 = (   - 2F*plane[0]*plane[2]);
	    reflectionMat.m03 = (   - 2F*plane[3]*plane[0]);

	    reflectionMat.m10 = (   - 2F*plane[1]*plane[0]);
	    reflectionMat.m11 = (1F - 2F*plane[1]*plane[1]);
	    reflectionMat.m12 = (   - 2F*plane[1]*plane[2]);
	    reflectionMat.m13 = (   - 2F*plane[3]*plane[1]);
	
    	reflectionMat.m20 = (   - 2F*plane[2]*plane[0]);
    	reflectionMat.m21 = (   - 2F*plane[2]*plane[1]);
    	reflectionMat.m22 = (1F - 2F*plane[2]*plane[2]);
    	reflectionMat.m23 = (   - 2F*plane[3]*plane[2]);

    	reflectionMat.m30 = 0F;
    	reflectionMat.m31 = 0F;
    	reflectionMat.m32 = 0F;
    	reflectionMat.m33 = 1F;
	}
}
