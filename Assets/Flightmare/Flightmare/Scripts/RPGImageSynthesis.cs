using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System.IO;
using System.Collections.Generic;


// From Unity ImageSynthesis
// @TODO:
// . support custom color wheels in optical flow via lookup textures
// . support custom depth encoding
// . support multiple overlay cameras
// . tests
// . better example scene(s)
// @KNOWN ISSUES
// . Motion Vectors can produce incorrect results in Unity 5.5.f3 when
//      1) during the first rendering frame
//      2) rendering several cameras with different aspect ratios - vectors do stretch to the sides of the screen

namespace RPGFlightmare
{
  // [RequireComponent (typeof(Camera))]
  public class RPGImageSynthesis : MonoBehaviour
  {
    public Shader uberReplacementShader;
    public Shader opticalFlowShader;
    public float opticalFlowSensitivity = 1.0f;
    // cached materials
    private Material opticalFlowMaterial;

    // pass configuration
    enum ReplacelementModes
    {
      ObjectId = 0,
      CatergoryId = 1,
      DepthCompressed = 2,
      DepthMultichannel = 3,
      Normals = 4,
      DepthRaw = 5,
      SemanticLabel = 6,
    };
    public Dictionary<string, bool> support_antialiasing = new Dictionary<string, bool>() { };
    public Dictionary<string, bool> needs_rescale = new Dictionary<string, bool>() { };

    public Dictionary<string, float> semantic_labels = new Dictionary<string, float>(){
      // start index from 1.0, keep 0.0 for unknown classes.
      // make `wall` and `roof` related together, and `objects` `floor` related together.
      // stand out `gate` as one category.
      {"roof", 1.0f},
      {"wall", 1.0f},
      {"_box", 1.0f},  // avoid conflicts with "toolbox" label
      {"trolley", 2.0f},
      {"toolbox", 2.0f},
      {"floor", 2.0f},
      {"generator", 2.0f},
      {"scaffold", 1.0f},
      {"pallet", 2.0f},
      {"lamp", 1.0f},
      {"frame", 1.0f},
      {"Flare", 1.0f},
      {"GroupLight", 1.0f},
      {"gate", 3.0f},
    };
    
    // change the list here for the enabled layers for unity bridge.
    // all available: "depth" "object_segment" "category_segment" "optical_flow" "semantic_label"
    // Note: you need to change the list in the Unity Editor to make it take effect !!!!
    // Changes of the list item here would not make any difference before you change the unity editor prefab.
    public List<string> image_modes = new List<string>() { "depth", "semantic_label", "optical_flow" };

    void Start()
    {
      // default fallbacks, if shaders are unspecified
      if (!uberReplacementShader)
        uberReplacementShader = Shader.Find("Hidden/UberReplacement");

      if (!opticalFlowShader)
        opticalFlowShader = Shader.Find("Hidden/OpticalFlow");

      // foreach (string layer in image_modes)
      // {
      //   Debug.Log("Stored image mode: " + layer);
      // }
    }

    static private void SetupCameraWithPostShader(Camera cam, Material material, DepthTextureMode depthTextureMode = DepthTextureMode.None)
    {
      var cb = new CommandBuffer();
      cb.Blit(null, BuiltinRenderTextureType.CurrentActive, material);
      cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
      cam.depthTextureMode = depthTextureMode;
    }
    static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode)
    {
      SetupCameraWithReplacementShader(cam, shader, mode, Color.black);
    }
    static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode, Color clearColor)
    {
      var cb = new CommandBuffer();
      cb.SetGlobalFloat("_OutputMode", (int)mode); // @TODO: CommandBuffer is missing SetGlobalInt() method
      // Debug.Log("Using the image mode here: " + (int)mode);
      cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
      cam.AddCommandBuffer(CameraEvent.BeforeFinalPass, cb);
      cam.SetReplacementShader(shader, "");
      cam.backgroundColor = clearColor;
      cam.clearFlags = CameraClearFlags.SolidColor;
    }
    public Camera CreateHiddenCamera(string cam_name, string image_mode, float camFOV, float nearClipPlane, float farClipPlane, Camera mainCam)
    {
      var go = new GameObject(cam_name, typeof(Camera));
      go.hideFlags = HideFlags.HideAndDontSave;
      go.transform.parent = mainCam.transform;
      //
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);

      var newCamera = go.GetComponent<Camera>();
      newCamera.fieldOfView = camFOV;
      newCamera.nearClipPlane = nearClipPlane;
      newCamera.farClipPlane = farClipPlane;
      
      updateCameraFilter(newCamera, image_mode);
      newCamera.targetDisplay = 1;
      return newCamera;
    }
    public void updateCameraFilter(Camera subcam, string image_mode)
    {
      // cache materials and setup material properties
      if (!opticalFlowMaterial || opticalFlowMaterial.shader != opticalFlowShader)
      {
        opticalFlowMaterial = new Material(opticalFlowShader);
        opticalFlowMaterial.SetFloat("_Sensitivity", opticalFlowSensitivity);
      }

      switch (image_mode)
      {
        // Setup Object Segmentation for the camera
        case "object_segment":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.ObjectId);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup Category Segmentation for the camera
        case "category_segment":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.CatergoryId);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup compressed depth for the camera
        case "depth":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.DepthRaw, Color.white);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = true;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup optical flow for the camera
        case "optical_flow": // TODO: color encoding does not work well.......
          SetupCameraWithPostShader(subcam, opticalFlowMaterial, DepthTextureMode.Depth | DepthTextureMode.MotionVectors);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        case "semantic_label":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.SemanticLabel, Color.white);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = true;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
      }
    }
    public void OnSceneChange()
    {
      var renderers = Object.FindObjectsOfType<Renderer>();
      var mpb = new MaterialPropertyBlock();
      foreach (var r in renderers)
      {
        var id = r.gameObject.GetInstanceID();
        var layer = r.gameObject.layer;
        var tag = r.gameObject.tag;
        
        string obj_name = r.gameObject.name;
        float semantic_label = 0.0f;

        // Debug.Log("Object id: " + id);
        // Debug.Log("Object name: " + obj_name);
        // Debug.Log("Layer id: " + layer);

        // TODO: this search could be expensive, change the game object names formatting
        foreach(KeyValuePair<string, float> element in semantic_labels){
          if (obj_name.Contains(element.Key)){
            // Debug.Log("found the semantic label of " + obj_name);
            // Debug.Log("its label is: " + element.Value);
            semantic_label = element.Value;
          }
        }

        mpb.SetColor("_ObjectColor", ColorEncoding.EncodeIDAsColor(id));
        mpb.SetColor("_CategoryColor", ColorEncoding.EncodeLayerAsColor(layer));
        mpb.SetFloat("_SemanticLabel", semantic_label);
        r.SetPropertyBlock(mpb);
      }
    }
    public byte[] getRawImage(Camera subcam, int width, int height, string image_mode)
    {
      // Debug.Log("current image mode is: " + image_mode);
      bool supportsAntialiasing = support_antialiasing[image_mode];
      bool needsRescale = needs_rescale[image_mode];
      var depth = 24;  // this magic number corresponds to RenderTextureFormat.RGBAUShort.
      var format = RenderTextureFormat.Default;
      if (image_mode == "depth" || image_mode == "semantic_label")
      {
        format = RenderTextureFormat.RFloat;
      }
      var readWrite = RenderTextureReadWrite.Default;
      var antiAliasing = (supportsAntialiasing) ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

      // render texture for current camera
      var finalRT =
          RenderTexture.GetTemporary(width, height, depth, format, readWrite, antiAliasing);

      var renderRT = (!needsRescale) ? finalRT :
          RenderTexture.GetTemporary(subcam.pixelWidth, subcam.pixelHeight, depth, format, readWrite, antiAliasing);
      var tex = new Texture2D(width, height, 
          (image_mode == "depth" || image_mode == "semantic_label") ? TextureFormat.RFloat : TextureFormat.RGB24, false);

      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;

      // render to offscreen texture (readonly from CPU side)
      RenderTexture.active = renderRT;
      subcam.targetTexture = renderRT;

      subcam.Render();
      if (needsRescale)
      {
        // blit to rescale (see issue with Motion Vectors in @KNOWN ISSUES)
        RenderTexture.active = finalRT;
        Graphics.Blit(renderRT, finalRT);
        RenderTexture.ReleaseTemporary(renderRT);
      }

      // read offsreen texture contents into the CPU readable texture
      tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
      tex.Apply();

      // encode texture into PNG
      var bytes = tex.EncodeToPNG();
      byte[] raw = tex.GetRawTextureData();
      // string filename = "/home/sysadmin/Desktop/" + cam_ID + ".png";
      // File.WriteAllBytes(filename, bytes);					

      // restore state and cleanup
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;

      Object.Destroy(tex);
      RenderTexture.ReleaseTemporary(finalRT);
      return raw;
    }
  }
}
