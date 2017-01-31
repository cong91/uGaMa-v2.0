using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;

namespace Plugins.Editor.JetBrains
{
  public class RiderAssetPostprocessor : AssetPostprocessor
  {
    private const string UnityPlayerProjectName = "Assembly-CSharp.csproj";
    private const string UnityEditorProjectName = "Assembly-CSharp-Editor.csproj";
    private const string UnityUnsafeKeyword = "-unsafe";
    private const string UnityDefineKeyword = "-define:";
    private const string PlayerProjectManualConfigRelativeFilePath = "smcs.rsp";
    private static readonly string  PlayerProjectManualConfigAbsoluteFilePath
      = Path.Combine(UnityEngine.Application.dataPath, PlayerProjectManualConfigRelativeFilePath);
    private const string EditorProjectManualConfigRelativeFilePath = "gmcs.rsp";
    private static readonly string  EditorProjectManualConfigAbsoluteFilePath
      = Path.Combine(UnityEngine.Application.dataPath, EditorProjectManualConfigRelativeFilePath);

    public static void OnGeneratedCSProjectFiles()
    {
      if (!RiderPlugin.Enabled)
        return;
      var currentDirectory = Directory.GetCurrentDirectory();
      var projectFiles = Directory.GetFiles(currentDirectory, "*.csproj");

      foreach (var file in projectFiles)
      {
        UpgradeProjectFile(file);
      }

      var slnFile = Directory.GetFiles(currentDirectory, "*.sln").First();
      RiderPlugin.Log(string.Format("Post-processing {0}", slnFile));
      string content = File.ReadAllText(slnFile);
      var lines = content.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
      var sb = new StringBuilder();
      foreach (var line in lines)
      {
        if (line.StartsWith("Project("))
        {
          MatchCollection mc = Regex.Matches(line, "\"([^\"]*)\"");
          //RiderPlugin.Log(mc[2].Value);
          sb.Append(line.Replace(mc[1].Value, GetFileNameWithoutExtension(mc[2].Value)+"\""));
        }
        else
        {
          sb.Append(line);
        }
        sb.Append(Environment.NewLine);
      }
      File.WriteAllText(slnFile,sb.ToString());
    }

    private static string GetFileNameWithoutExtension(string path)
    {
      if (path == null)
        return (string) null;
      int length;
      if ((length = path.LastIndexOf('.')) == -1)
        return path;
      return path.Substring(0, length);
    }

    private static void UpgradeProjectFile(string projectFile)
    {
      RiderPlugin.Log(string.Format("Post-processing {0}", projectFile));
      var doc = XDocument.Load(projectFile);
      var projectContentElement = doc.Root;
      XNamespace xmlns = projectContentElement.Name.NamespaceName; // do not use var

      FixTargetFrameworkVersion(projectContentElement, xmlns);
      SetLangVersion(projectContentElement, xmlns);
      SetManuallyDefinedComilingSettings(projectFile, projectContentElement, xmlns);

      SetXCodeDllReference("UnityEditor.iOS.Extensions.Xcode.dll", xmlns, projectContentElement);
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Common.dll", xmlns, projectContentElement);

      doc.Save(projectFile);
    }

    private static void SetManuallyDefinedComilingSettings(string projectFile, XElement projectContentElement, XNamespace xmlns)
    {
      string configPath;

      if (IsPlayerProjectFile(projectFile))
        configPath = PlayerProjectManualConfigAbsoluteFilePath;
      else if (IsEditorProjectFile(projectFile))
        configPath = EditorProjectManualConfigAbsoluteFilePath;
      else
        configPath = null;

      if(!string.IsNullOrEmpty(configPath))
        ApplyManualCompilingSettings(configPath
          , projectContentElement
          , xmlns);
    }

    private static void ApplyManualCompilingSettings(string configFilePath, XElement projectContentElement, XNamespace xmlns)
    {
      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UnityUnsafeKeyword))
        {
          // Add AllowUnsafeBlocks to the .csproj. Unity doesn't generate it (although VSTU does).
          // Strictly necessary to compile unsafe code
          ApplyAllowUnsafeBlocks(projectContentElement, xmlns);
        }
        if (configText.Contains(UnityDefineKeyword))
        {
          // defines could be
          // 1) -define:DEFINE1,DEFINE2
          // 2) -define:DEFINE1;DEFINE2
          // 3) -define:DEFINE1 -define:DEFINE2
          // 4) -define:DEFINE1,DEFINE2;DEFINE3
          // tested on "-define:DEF1;DEF2 -define:DEF3,DEF4;DEFFFF \n -define:DEF5"
          // result: DEF1, DEF2, DEF3, DEF4, DEFFFF, DEF5

          var definesList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UnityDefineKeyword))
            {
              var defineEndPos = f.IndexOf(UnityDefineKeyword) + UnityDefineKeyword.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              definesList.AddRange(definesSubString.Split(','));
            }
          }

          //UnityEngine.Debug.Log(string.Join(", ",definesList.ToArray()));
          ApplyCustomDefines(definesList.ToArray(), projectContentElement, xmlns);
        }
      }
    }

    private static void ApplyCustomDefines(string[] customDefines, XElement projectContentElement, XNamespace xmlns)
    {
      var definesString = string.Join(";", customDefines);

      var defineConstants = projectContentElement
        .Elements(xmlns+"PropertyGroup")
        .Elements(xmlns+"DefineConstants")
        .FirstOrDefault(definesConsts=> !string.IsNullOrEmpty(definesConsts.Value));

      if (defineConstants != null)
      {
        defineConstants.SetValue(defineConstants.Value + ";" + definesString);
      }
    }

    private static void ApplyAllowUnsafeBlocks(XElement projectContentElement, XNamespace xmlns)
    {
      projectContentElement.AddFirst(
        new XElement(xmlns + "PropertyGroup", new XElement(xmlns + "AllowUnsafeBlocks", true)));
    }

    private static bool IsPlayerProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UnityPlayerProjectName;
    }

    private static bool IsEditorProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UnityEditorProjectName;
    }

    // Helps resolve System.Linq under mono 4 - RIDER-573
    private static void FixTargetFrameworkVersion(XElement projectElement, XNamespace xmlns)
    {
      if (!RiderPlugin.TargetFrameworkVersion45)
        return;

      var targetFrameworkVersion = projectElement.Elements(xmlns + "PropertyGroup").
        Elements(xmlns + "TargetFrameworkVersion").First();
      var version = new Version(targetFrameworkVersion.Value.Substring(1));
      if (version < new Version(4, 5))
        targetFrameworkVersion.SetValue("v4.5");
    }

    private static void SetLangVersion(XElement projectElement, XNamespace xmlns)
    {
      // Add LangVersion to the .csproj. Unity doesn't generate it (although VSTU does).
      // Not strictly necessary, as the Unity plugin for Rider will work it out, but setting
      // it makes Rider work if it's not installed.
      projectElement.AddFirst(new XElement(xmlns + "PropertyGroup",
        new XElement(xmlns + "LangVersion", GetLanguageLevel())));
    }

    private static string GetLanguageLevel()
    {
      // https://bitbucket.org/alexzzzz/unity-c-5.0-and-6.0-integration/src
      if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "CSharp70Support")))
        return "7";
      if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "CSharp60SUpport")))
        return "6";

      // Unity 5.5 supports C# 6, but only when targeting .NET 4.6. The enum doesn't exist pre Unity 5.5
      if ((int)PlayerSettings.apiCompatibilityLevel >= 3)
        return "6";

      return "4";
    }

    private static void SetXCodeDllReference(string name, XNamespace xmlns, XElement projectContentElement)
    {
      string unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);

      var xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("Data/PlaybackEngines/iOSSupport", name));
      if (!File.Exists(xcodeDllPath))
        xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("PlaybackEngines/iOSSupport", name));

      if (File.Exists(xcodeDllPath))
      {
        var itemGroup = new XElement(xmlns + "ItemGroup");
        var reference = new XElement(xmlns + "Reference");
        reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(xcodeDllPath)));
        reference.Add(new XElement(xmlns + "HintPath", xcodeDllPath));
        itemGroup.Add(reference);
        projectContentElement.Add(itemGroup);
      }
    }
  }
}