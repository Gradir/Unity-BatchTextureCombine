#if UNITY_EDITOR
/*
Combine Textures - an editor window for batch finding and combining
Metallic, Occlusion and Roughness textures into single new texture's channels

Made by Piotr "Gradir" Kołodziejczyk (gradir@gmail.com)
based on Channel Packer by Camobiwon (https://github.com/camobiwon/ChannelPacker)
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TechArtTest
{
    public class CombineTexturesEditorWindow : EditorWindow
    {
	    // ===== Editor Window =====
	    SerializedObject _serializedObject;
	    SerializedProperty _texturesToCombine;
	    SerializedProperty _shaderForPackedTextures;
	    SerializedProperty _textureFolders;
	    SerializedProperty _metallicTargetChannel;
	    SerializedProperty _occlusionTargetChannel;
	    SerializedProperty _roughnessTargetChannel;
	    SerializedProperty _metallicSuffixes;
	    SerializedProperty _occlusionSuffixes;
	    SerializedProperty _roughnessSuffixes;
	    SerializedProperty _materialFolders;
	    GUIStyle _labelStyle;
	    Vector2 _scrollPosition;
	    bool _settingsFoldout;
	    
	    // ===== Texture Search =====
        public Texture2D[] TexturesToCombine;
        public Shader ShaderForPackedTextures;
        public string[] TextureFolderPaths = { "Assets/techart/maps"};
        
        public ColorChannel MetallicTargetChannel = ColorChannel.R;
        public ColorChannel OcclusionTargetChannel = ColorChannel.G;
        public ColorChannel RoughnessTargetChannel = ColorChannel.B;
        // Default values for channels if texture is missing - occlusion and alpha defaults to white
        public Vector4 Defaults = new( 0, 1, 0, 1 );
        public string ShaderPropertyForPackedTextures = "_MetOccRoughMap";
	        
        // We add [NonReorderable] tag because those properties are displayed inside a Foldout Layout Group,
        // and Unity now uses Foldout Groups to display those arrays in the inspector
        // but Foldout Groups cannot be nested.
        [NonReorderable] public string[] MetallicSuffixes = new[] { "_metal" };
        [NonReorderable] public string[] RoughnessSuffixes = new[] { "_rough" };
        [NonReorderable] public string[] OcclusionSuffixes = new[] { "_AmbientOcclusion", "_AO"};
        [NonReorderable] public string[] MaterialFolderPaths = new[] { "Assets/techart/materials"};
        
        // The compute shader reference is set through this script's inspector
        [SerializeField] ComputeShader _fastPack;
        
        TextureSetWithMaps _currentlyProcessedTextureSet;
        List<TextureSetWithMaps> _allTextureSets = new();
        int _selectedMode;
	    int _packedTextureShaderProperty;
        string _currentFolder;
     
        // ===== Texture Packing =====
        readonly float[] _mults = { 1, 1, 1, 1 };
        readonly bool[] _inverts = new bool[4];
        readonly ColorChannel[] _froms = new ColorChannel[4];
        readonly RenderTexture[] _blits = new RenderTexture[4];
	    readonly int _packed = Shader.PropertyToID("Packed");
	    readonly int _packedCol = Shader.PropertyToID("packedCol");
	    readonly int _fromsId = Shader.PropertyToID("froms");
        readonly int _invertsId = Shader.PropertyToID("inverts");
        readonly int _multsId = Shader.PropertyToID("mults");
        readonly int _resultId = Shader.PropertyToID("Result");
        readonly int _rId = Shader.PropertyToID("r");
        readonly int _gId = Shader.PropertyToID("g");
        readonly int _bId = Shader.PropertyToID("b");
        readonly int _aId = Shader.PropertyToID("a");
	    
	    readonly string[] _modes = { "Folders", "Specific Textures" };
	    const string TextureSearch = "t: Texture2D";
	    const string MaterialSearch = "t: material";
	    const string ChannelSet = "ChannelSet";
	    const string Csmain = "CSMain";
	    const char Separator = '_';
	    const char Slash = '/';
        
        [MenuItem("Assets/Combine Textures..", false, 0)]
        [MenuItem("Tools/Combine Textures...")]
        public static void ShowWindow()
        {
	        var window = (CombineTexturesEditorWindow)GetWindow(typeof(CombineTexturesEditorWindow));
	        window.titleContent = new GUIContent("Combine Textures");
	        window.Show();
        }

        [MenuItem("Assets/Combine Textures..", true)]
        public static bool ShowWindowValidate()
        {
	        return Selection.activeObject is Texture2D;
        }
        
        void OnEnable()
        {
            var target = this;
            _serializedObject = new SerializedObject(target);
            _texturesToCombine = _serializedObject.FindProperty("TexturesToCombine");
            _shaderForPackedTextures = _serializedObject.FindProperty("ShaderForPackedTextures");
            _textureFolders = _serializedObject.FindProperty("TextureFolderPaths");
            _metallicTargetChannel = _serializedObject.FindProperty("MetallicTargetChannel");
            _occlusionTargetChannel = _serializedObject.FindProperty("OcclusionTargetChannel");
            _roughnessTargetChannel = _serializedObject.FindProperty("RoughnessTargetChannel");
            _metallicSuffixes = _serializedObject.FindProperty("MetallicSuffixes");
            _roughnessSuffixes = _serializedObject.FindProperty("RoughnessSuffixes");
            _occlusionSuffixes = _serializedObject.FindProperty("OcclusionSuffixes");
            _materialFolders = _serializedObject.FindProperty("MaterialFolderPaths");
            ConfigureStyles();
        }

        void OnGUI()
        {
            GUI.backgroundColor = Styles.BackgroundColor;
            
            EditorGUILayout.BeginVertical();
            
            _selectedMode = EditorGUILayout.Popup("Choose mode", _selectedMode, _modes);
            _scrollPosition =
                GUILayout.BeginScrollView(_scrollPosition, false, true, GUILayout.ExpandHeight(false));
            EditorGUILayout.HelpBox("Source textures' compression should be disabled for this process",
	            MessageType.Info);
            switch ((TextureChoosingMode)_selectedMode)
            {
                default:
                case TextureChoosingMode.Folders:
                    EditorGUILayout.PropertyField(_textureFolders, true);
                    break;
                case TextureChoosingMode.SpecificTextures:
                    EditorGUILayout.PropertyField(_texturesToCombine, true);
                    break;
            }
            
            GUILayout.EndScrollView();

            _settingsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_settingsFoldout, "Settings");
            if (_settingsFoldout)
            {
                EditorGUILayout.LabelField("Target Channels", _labelStyle);
                EditorGUILayout.PropertyField(_metallicTargetChannel, false);
                EditorGUILayout.PropertyField(_occlusionTargetChannel, false);
                EditorGUILayout.PropertyField(_roughnessTargetChannel, false);
                EditorGUILayout.Vector4Field("Defaults - fallback color for missing textures per channel:", Defaults);
                EditorGUILayout.LabelField("Texture naming", _labelStyle);
                EditorGUILayout.PropertyField(_metallicSuffixes, true);
                EditorGUILayout.PropertyField(_roughnessSuffixes, true);
                EditorGUILayout.PropertyField(_occlusionSuffixes, true);
                EditorGUILayout.LabelField("Folders with materials to process", _labelStyle);
                EditorGUILayout.PropertyField(_materialFolders, true);
                EditorGUILayout.LabelField("Shader for packed textures", _labelStyle);
                EditorGUILayout.PropertyField(_shaderForPackedTextures, false);
                EditorGUILayout.TextField("Shader property id:", ShaderPropertyForPackedTextures);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            _serializedObject.ApplyModifiedProperties();
	        _packedTextureShaderProperty = Shader.PropertyToID(ShaderPropertyForPackedTextures);

            if (GUILayout.Button("Create Combined Textures And Assign to Materials"))
            {
                if (_selectedMode == (int)TextureChoosingMode.Folders)
                    ProcessTexturesInFolders();
                else if (_selectedMode == (int)TextureChoosingMode.SpecificTextures)
                    ProcessSpecificTextureAssets();
            }
            EditorGUILayout.EndVertical();
            
        }
        
        void ProcessTexturesInFolders()
        {
	        ResetTextureSets();
	        if (TextureFolderPaths == null || TextureFolderPaths.Length == 0)
	        {
		        Debug.Log("<size=15><color=red>Please provide at least one folder path</color></size>");
		        return;
	        }
	        for (var i = 0; i < TextureFolderPaths.Length; i++)
	        {
		        if (string.IsNullOrEmpty(TextureFolderPaths[i]))
		        {
			        Debug.Log("<size=15><color=red>Empty folder path</color></size>");
			        continue;
		        }
		        _currentFolder = TextureFolderPaths[i];

		        var folderTextures = GetAllTexturesInFolder(_currentFolder);
		        foreach (var texture in folderTextures)
			        ProcessTexture(texture);

		        CreatePackedTexturesAndAssignToMaterials();
	        }
        }

        void ProcessSpecificTextureAssets()
        {
            ResetTextureSets();
            if (TexturesToCombine == null || TexturesToCombine.Length == 0)
            {
	            Debug.Log("<size=15><color=red>Please provide textures</color></size>");
	            return;
            }
            
            for (var i = 0; i < TexturesToCombine.Length; i++)
            {
	            var texture = TexturesToCombine[i];
	            if (texture == null)
	            {
					Debug.Log("<size=15><color=red>Null texture reference</color></size>");
		            continue;
	            }
	            _currentFolder = AssetDatabase.GetAssetPath(texture);
	            _currentFolder = _currentFolder.Substring(0, _currentFolder.LastIndexOf(Slash));
	            ProcessTexture(texture);
            }

            CreatePackedTexturesAndAssignToMaterials();
        }

        void ResetTextureSets() => _allTextureSets = new List<TextureSetWithMaps>();

        void CreatePackedTexturesAndAssignToMaterials()
        {
	        for (var i = 0; i < _allTextureSets.Count; i++)
	        {
		        var textureSetWithMaps = _allTextureSets[i];
		        var packedTextureAsset = CreateAndSavePackedTexture(textureSetWithMaps);
		        if (packedTextureAsset != null)
			        ProcessAssigningPackedTextureToMaterial(packedTextureAsset, textureSetWithMaps);
	        }
        }

        void ProcessTexture(Texture2D texture2D)
        {
	        var textureName = texture2D.name;
	        var textureSetName = TryGettingTextureSetName(textureName);
                    
	        if (ReferenceEquals(_currentlyProcessedTextureSet, null) || string.Equals(_currentlyProcessedTextureSet.TextureSetName, textureSetName) == false)
		        _currentlyProcessedTextureSet = new(textureSetName);
                    
	        var textureMapType = GetTextureMapTypeBySuffix(textureName);
	        if (textureMapType is TextureMapType.None)
		        return;
	        AssignTextureByTextureMapType(textureMapType, texture2D);

	        if (_allTextureSets.Contains(_currentlyProcessedTextureSet) == false)
		        _allTextureSets.Add(_currentlyProcessedTextureSet);
        }

        void AssignTextureByTextureMapType(TextureMapType textureMapType, Texture2D texture2D)
        {
	        var channelInput = (int)GetChannelByTextureMapType(textureMapType);
	        _currentlyProcessedTextureSet.Inputs[channelInput] = texture2D;
	        
	        if (_currentlyProcessedTextureSet.TextureDimensions == Vector2Int.zero)
	        {
		        var inputTexture = _currentlyProcessedTextureSet.Inputs[channelInput];
		        _currentlyProcessedTextureSet.TextureDimensions.x = inputTexture.width;
		        _currentlyProcessedTextureSet.TextureDimensions.y = inputTexture.height;
	        }
        }

        Texture2D[] GetAllTexturesInFolder(string folderPath)
        {
            return AssetDatabase.FindAssets(TextureSearch, new[] { folderPath })
                .Select(guid => AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid)))
                .ToArray();
        }

        string TryGettingTextureSetName(string textureName)
        {
	        var indexOfLastSeparator = textureName.LastIndexOf(Separator);
	        return indexOfLastSeparator == 0 ? textureName : textureName.Substring(0, indexOfLastSeparator);
        }

        TextureMapType GetTextureMapTypeBySuffix(string textureName)
        {
	        if (CheckIfTextureNameContainsPhrase(textureName, MetallicSuffixes))
				return TextureMapType.Metallic;
	        if (CheckIfTextureNameContainsPhrase(textureName, OcclusionSuffixes))
		        return TextureMapType.Occlusion;
	        if (CheckIfTextureNameContainsPhrase(textureName, RoughnessSuffixes))
		        return TextureMapType.Roughness;

	        return TextureMapType.None;
        }

        bool CheckIfTextureNameContainsPhrase(string textureName, string[] suffixesToCheck)
        {
	        for (var i = 0; i < suffixesToCheck.Length; i++)
	        {
		        if (textureName.Contains(suffixesToCheck[i], StringComparison.InvariantCultureIgnoreCase))
			        return true;
	        }

	        return false;
        }

        // TODO Add ability to rebind this
        ColorChannel GetChannelByTextureMapType(TextureMapType mapType) => mapType switch
        {
	        TextureMapType.Metallic => MetallicTargetChannel,
	        TextureMapType.Occlusion => OcclusionTargetChannel,
	        TextureMapType.Roughness => RoughnessTargetChannel,
	        _ => ColorChannel.R
        };
		
		Texture2D CreateAndSavePackedTexture(TextureSetWithMaps textureSet)
		{
			var textureDimensions = textureSet.TextureDimensions;
			var inputs = textureSet.Inputs;
			
			var finalTexture = new Texture2D(textureDimensions.x, textureDimensions.y, TextureFormat.ARGB32, false,
				true);
			var blitKernel = _fastPack.FindKernel(ChannelSet);

			PackTexture(0); //Red
			PackTexture(1); //Green
			PackTexture(2); //Blue
			PackTexture(3); //Alpha

			//Prepare textures for packing
			void PackTexture(int channelInput)
			{
				_blits[channelInput] = new RenderTexture(textureDimensions.x, textureDimensions.y, 0,
					RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				if (inputs[channelInput] != null)
					Graphics.Blit(inputs[channelInput], _blits[channelInput]);
				else
				{
					_blits[channelInput].enableRandomWrite = true;
					_blits[channelInput].Create();

					_fastPack.SetTexture(blitKernel, _packed, _blits[channelInput]);
					_fastPack.SetFloat(_packedCol, Defaults[channelInput]);
					_fastPack.Dispatch(blitKernel, textureDimensions.x, textureDimensions.y, 1);
				}
			}

			if (textureDimensions != Vector2Int.zero)
			{
				var packedTexture = new RenderTexture(textureDimensions.x, textureDimensions.y, 32,
					RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				packedTexture.enableRandomWrite = true;
				packedTexture.Create();

				//Send textures to compute shader
				var kernel = _fastPack.FindKernel(Csmain);
				_fastPack.SetTexture(kernel, _resultId, packedTexture);
				
				_fastPack.SetTexture(kernel, _rId, _blits[0]);
				_fastPack.SetTexture(kernel, _gId, _blits[1]);
				_fastPack.SetTexture(kernel, _bId, _blits[2]);
				_fastPack.SetTexture(kernel, _aId, _blits[3]);

				//Send data to compute shader for processing
				_fastPack.SetInts(_fromsId, inputs[0] ? (int)_froms[0] : 0, inputs[1] ? (int)_froms[1] : 0,
					inputs[2] ? (int)_froms[2] : 0, inputs[3] ? (int)_froms[3] : 0);
				_fastPack.SetInts(_invertsId, inputs[0] ? (_inverts[0] ? 1 : 0) : 0, inputs[1] ? (_inverts[1] ? 1 : 0) : 0,
					inputs[2] ? (_inverts[2] ? 1 : 0) : 0, inputs[3] ? (_inverts[3] ? 1 : 0) : 0);
				_fastPack.SetFloats(_multsId, inputs[0] ? _mults[0] : 1, inputs[1] ? _mults[1] : 1,
					inputs[2] ? _mults[2] : 1, inputs[3] ? _mults[3] : 1);
				_fastPack.Dispatch(kernel, textureDimensions.x, textureDimensions.y, 1);

				var previous = RenderTexture.active;
				RenderTexture.active = packedTexture;
				finalTexture.ReadPixels(new Rect(0, 0, packedTexture.width, packedTexture.height), 0, 0);
				finalTexture.Apply();
				RenderTexture.active = previous;
			}
			
			return SaveTexture(finalTexture, textureSet.TextureSetName);
		}
		
		Texture2D SaveTexture(Texture2D finalTexture, string textureName)
		{
			var path = _currentFolder + Slash + textureName + "_packed" + ".png";
			byte[] pngData = finalTexture.EncodeToPNG();

			if (path.Length != 0 && pngData != null)
			{
				File.WriteAllBytes(path, pngData);
				Debug.Log($"Packed texture saved to: {path}");
				AssetDatabase.Refresh();
				TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
				importer.sRGBTexture = false;
				importer.SaveAndReimport();
				return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
			}
			
			return null;
		}

		void ProcessAssigningPackedTextureToMaterial(Texture2D packedTexture, TextureSetWithMaps textureSetWithMaps)
		{
			var materialGuids = AssetDatabase.FindAssets(MaterialSearch, MaterialFolderPaths);
			for (var i = 0; i < materialGuids.Length; i++)
			{
				var materialAsset = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(materialGuids[i]));
				if (materialAsset != null)
				{
					var texturePropertyNameIds = materialAsset.GetTexturePropertyNameIDs();
					var foundMatchingTexture = false;
					for (var j = 0; j < texturePropertyNameIds.Length; j++)
					{
						var textureAsset = materialAsset.GetTexture(texturePropertyNameIds[j]);
						if (textureAsset != null && textureAsset.name.Contains(textureSetWithMaps.TextureSetName))
						{
							Debug.Log($"<size=15><color=white>Processing: {textureAsset.name}</color></size>");
							// Got a material using this texture set,
							// now we can break out of this loop and proceed with shader and texture reference changes
							foundMatchingTexture = true;
							break;
						}
					}
					if (foundMatchingTexture)
					{
						materialAsset.shader = ShaderForPackedTextures;
						materialAsset.SetTexture(_packedTextureShaderProperty, packedTexture);
						AssetDatabase.SaveAssets();
						Debug.Log($"<size=15><color=green>Assigned packed texture to: {materialAsset.name} material</color></size>");
					}
				}
			}
		}

		void ConfigureStyles()
		{
			_labelStyle = new GUIStyle();
			_labelStyle.fontSize = 14;
			_labelStyle.normal.textColor = Color.white;
		}
    }
    
    [Serializable]
    public class TextureSetWithMaps
    {
	    public string TextureSetName;
	    public Vector2Int TextureDimensions;
	    public Texture2D[] Inputs = new Texture2D[4];

	    public TextureSetWithMaps(string textureSetName)
	    {
		    TextureSetName = textureSetName;
	    }
    }

    public enum TextureChoosingMode
    {
        Folders,
        SpecificTextures
    }
    
    public enum ColorChannel
    {
	    R,
	    G,
	    B,
	    A
    }
    
    public enum TextureMapType
    {
	    None,
	    Metallic,
	    Occlusion,
	    Roughness
    }
    
    static class Styles
    {
	    public static Color BackgroundColor = new(1f, 0.91f, 0.64f);
    }
}
#endif