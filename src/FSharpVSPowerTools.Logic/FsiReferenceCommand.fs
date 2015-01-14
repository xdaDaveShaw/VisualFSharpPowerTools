﻿namespace FSharpVSPowerTools.Reference

open FSharpVSPowerTools
open EnvDTE80
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Shell.Interop
open System
open EnvDTE
open FSharpVSPowerTools.ProjectSystem
open System.IO
open VSLangProj
open System.ComponentModel.Design
open System.Text
open System.Runtime.Versioning

type FsiReferenceCommand(dte2: DTE2, mcs: OleMenuCommandService, _shell: IVsUIShell) =
    static let scriptFolderName = "Scripts"
    static let headerComment = "// Warning: generated file; your changes could be lost when a new file is generated."
    
    let containsReferenceScript (project: Project) = 
        project.ProjectItems.Item(scriptFolderName) 
        |> Option.ofNull
        |> Option.bind (fun scriptItem -> Option.ofNull scriptItem.ProjectItems)
        |> Option.map (fun projectItems -> 
            projectItems
            |> Seq.cast<ProjectItem>
            |> Seq.exists (fun item -> item.Name.Contains("load-references")))
        |> Option.getOrElse false

    let getActiveProject() =
        let dte = dte2 :?> DTE
        dte.ActiveSolutionProjects :?> obj []
        |> Seq.tryHead
        |> Option.map (fun o -> o :?> Project)

    let getProjectFolder(project: Project) =
        project.Properties.Item("FullPath").Value.ToString()

    let getFullFilePathInScriptFolder project fileName = 
        let projectFolder = getProjectFolder project
        let scriptFolder = projectFolder </> scriptFolderName
        if not (Directory.Exists scriptFolder) then
            Directory.CreateDirectory(scriptFolder) |> ignore
        scriptFolder </> fileName

    let addFileToActiveProject(project: Project, fileName: string, content: string) = 
        if isFSharpProject project then
            let textFile = getFullFilePathInScriptFolder project fileName
            use writer = File.CreateText(textFile)
            writer.Write(content)

            let projectFolderScript = 
                project.ProjectItems.Item(scriptFolderName)
                |> Option.ofNull
                |> Option.getOrTry (fun _ ->
                    project.ProjectItems.AddFolder(scriptFolderName))
            projectFolderScript.ProjectItems.AddFromFile(textFile) |> ignore
            project.Save()

    let getActiveProjectOutput (project: Project) =
        Option.attempt <| fun _ ->
            let projectPath = getProjectFolder project
            let outputFileName = project.Properties.Item("OutputFileName").Value.ToString()
            let config = project.ConfigurationManager.ActiveConfiguration
            let outputPath = config.Properties.Item("OutputPath").Value.ToString()
            projectPath </> outputPath </> outputFileName

    let getActiveOutputFileFullPath (reference: Reference) =
        reference.GetType().GetProperty "SourceProject"
        |> Option.ofNull
        |> Option.map (fun sourceProject -> sourceProject.GetValue(reference) :?> Project)
        |> Option.bind getActiveProjectOutput

    let getRelativePath (folder: string) (file: string) =
        let fileUri = Uri file
        // Folders must end in a slash
        let folder =
            if not <| folder.EndsWith (Path.DirectorySeparatorChar.ToString()) then
                folder + string Path.DirectorySeparatorChar
            else folder
        let folderUri = Uri folder
        Uri.UnescapeDataString(folderUri.MakeRelativeUri(fileUri).ToString().Replace('/', Path.DirectorySeparatorChar))

    /// Remove the temporary attribute file name generated by F# compiler
    let filterTempAttributeFileName (project: Project) sourceFiles =
        Option.attempt (fun _ ->
            let targetFrameworkMoniker = project.Properties.Item("TargetFrameworkMoniker").Value.ToString()
            sprintf "%s.AssemblyAttributes.fs" targetFrameworkMoniker) 
        |> Option.map (fun tempAttributeFileName -> 
            sourceFiles |> Array.filter (fun fileName -> Path.GetFileNameSafe(fileName) <> tempAttributeFileName))
        |> Option.getOrElse sourceFiles

    let generateLoadScriptContent(project: Project, scriptFile: string) =
        let scriptFolder = getProjectFolder project </> scriptFolderName
        use projectProvider = new ProjectProvider(project, (fun _ -> None), (fun _ -> ()), id)
        let sb = StringBuilder()
        sb.AppendLine(headerComment) |> ignore
        sb.AppendLine(sprintf "#load @\"%s\"" scriptFile) |> ignore
        match filterTempAttributeFileName project (projectProvider :> IProjectProvider).SourceFiles with
        | [||] -> ()
        | sourceFiles ->
            sb.Append("#load ") |> ignore
            let relativePaths = sourceFiles |> Array.map (getRelativePath scriptFolder >> sprintf "@\"%s\"")
            sb.AppendLine(String.Join(Environment.NewLine + new String(' ', "#load ".Length), relativePaths)) |> ignore
        sb.ToString()   

    let isReferenceProject (reference: Reference) =
        let sourceProject = reference.GetType().GetProperty("SourceProject")
        sourceProject <> null && sourceProject.GetValue(reference) <> null

    let getReferenceAssembliesFolderByVersion (project: Project) =
        Option.attempt (fun _ ->
            let targetFrameworkMoniker = project.Properties.Item("TargetFrameworkMoniker").Value.ToString()
            let frameworkVersion = FrameworkName(targetFrameworkMoniker).Version.ToString()
            let programFiles = 
                match Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) with
                | null -> Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                | s -> s 
            sprintf @"%s\Reference Assemblies\Microsoft\Framework\.NETFramework\v%s" programFiles frameworkVersion)

    let generateRefs (project: Project) =
        let excludingList = set [| "FSharp.Core"; "mscorlib" |]
        let assemblyRefList = ResizeArray()
        let projectRefList = ResizeArray()

        match project.Object with
        | :? VSProject as vsProject ->
            let scriptFolder = getProjectFolder project </> scriptFolderName
            let referenceAssembliesFolder = getReferenceAssembliesFolderByVersion project
            vsProject.References
            |> Seq.cast<Reference>
            |> Seq.iter (fun reference -> 
                if not (excludingList.Contains reference.Name) then
                    if isReferenceProject reference then
                        getActiveOutputFileFullPath reference
                        |> Option.iter (getRelativePath scriptFolder >> projectRefList.Add)
                    else
                        let fullPath = reference.Path
                        if File.Exists fullPath then
                            let referenceFolder = Path.GetDirectoryName fullPath
                            match referenceAssembliesFolder with
                            | Some referenceAssembliesFolder ->
                                if String.Equals(Path.GetFullPathSafe referenceAssembliesFolder, 
                                                 Path.GetFullPathSafe referenceFolder, StringComparison.OrdinalIgnoreCase) then
                                    assemblyRefList.Add(Path.GetFileName fullPath)
                                else
                                    assemblyRefList.Add(getRelativePath scriptFolder fullPath)
                            | _ -> assemblyRefList.Add(getRelativePath scriptFolder fullPath))
        | _ -> ()

        assemblyRefList 
        |> Seq.append projectRefList
        |> Seq.map (sprintf "#r @\"%s\"") 
        |> Seq.toList

    let generateFileContent (refLines: #seq<string>) =
        String.Join(Environment.NewLine, headerComment, String.Join(Environment.NewLine, refLines))

    let tryGetExistingFileRefs project fileName = 
        let filePath = getFullFilePathInScriptFolder project fileName  
        if File.Exists filePath then
            File.ReadLines filePath
            |> Seq.filter (fun (line: string) -> line.StartsWith "#r")
            |> Seq.toList
            |> Some
        else None

    let mergeRefs existing actual =
        // remove refs which don't actual anymore (they have been removed from the project)
        let existing =
            existing |> List.filter (fun existingRef -> List.exists ((=) existingRef) actual)
        // get refs which don't exist in the existing file
        let newExtraRefs =
            actual |> List.filter (fun actualRef -> not <| List.exists ((=) actualRef) existing)
        // concatenate old survived refs and the extra ones
        existing @ newExtraRefs

    let generateFile (project: Project) =
        Option.ofNull project
        |> Option.iter (fun project ->
            let loadRefsFileName = "load-references.fsx"
            let actualRefs = generateRefs project
            let refs = 
                match tryGetExistingFileRefs project loadRefsFileName with
                | None -> actualRefs
                | Some existingRefs -> mergeRefs existingRefs actualRefs
            addFileToActiveProject(project, loadRefsFileName, generateFileContent refs)
            let content = generateLoadScriptContent(project, "load-references.fsx")
            addFileToActiveProject(project, "load-project.fsx", content))

    let generateReferencesForFsi() =
        getActiveProject()
        |> Option.iter (fun project ->
            // Generate script files
            if isFSharpProject project then
                generateFile project)        

    let onBuildDoneHandler = EnvDTE._dispBuildEvents_OnBuildDoneEventHandler (fun _ _ ->
            dte2.Solution.Projects
            |> Seq.cast<Project>
            |> Seq.iter (fun project ->
                if isFSharpProject project && containsReferenceScript project then
                    generateFile project))

    let events = dte2.Events.BuildEvents
    do events.add_OnBuildDone onBuildDoneHandler

    member __.SetupCommands() =
        let menuCommandID = CommandID(Constants.guidGenerateReferencesForFsiCmdSet, int Constants.cmdidGenerateReferencesForFsi)
        let menuCommand = OleMenuCommand((fun _ _ -> generateReferencesForFsi()), menuCommandID)
        menuCommand.BeforeQueryStatus.Add (fun _ -> 
            let visibility = getActiveProject() |> Option.map isFSharpProject |> Option.getOrElse false
            menuCommand.Visible <- visibility)
        mcs.AddCommand(menuCommand)

    interface IDisposable with
        member __.Dispose() = 
            events.remove_OnBuildDone onBuildDoneHandler
        
