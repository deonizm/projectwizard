﻿using System;
using System.Collections.Generic;
using System.Text;
using EnvDTE;
using EnvDTE80;
using System.Windows.Forms;
using System.IO;
using System.Reflection;

namespace ProjectWizard
{
	public enum ProjectType
	{
		ConsoleApp = 0,
		WINApp = 1,
		WTLApp = 2,
		DLLApp = 3,
		LIBApp = 4,
		SYSApp = 5,
	}

    public class Wiz : IDTWizard
    {
		// Basic class variables for object Wizard
		//TODO: Most or all of these may be within WizardData. Haven't looked much into what all info you grab
		protected _DTE dte = null;
		protected WizardData wz = null;
		protected string solutionName = null;
		protected string projectName = null;
		protected string path = null;
		protected string solutionPath = null;
		protected string projectPath = null;
		protected ProjectType projectType;
		protected bool createNewSolution = false;
		protected string includes = null;
		protected string addlIncludeDirs = null;
		protected string addlLibDirs = null;

		public static readonly string[] ProjectTypeStrings = new string[6]
		{
			"ConsoleApp",
			"WINApp",
			"WTLApp",
			"DLLApp",
			"LIBApp",
			"SYSApp",
		};

		private static Guid vsWizardNewProject = new Guid( "{0F90E1D0-4999-11D1-B6D1-00A0C90F2744}" );		//Adds new project to a solution
		private static Guid vsWizardAddItem = new Guid( "{0F90E1D1-4999-11D1-B6D1-00A0C90F2744}" );			//Adds a subproject to an existing project
		private static Guid vsWizardAddSubproject = new Guid( "{0F90E1D2-4999-11D1-B6D1-00A0C90F2744}" );	//Adds an item to an existing project

        // Execute is the main entry point for a project wizard.  It has to follow this template.
		// contextParams:
		// 0: Wizard Type GUID
		// 1: Project Name: A string that is the unique VS project name
		// 2: Local Directory: Local Location of working project files
		// 3: Installation Directory: Directory path of the VS installation
		// 4: Create New Solution : true..... Add to existing solution: false
		// 5: *WRONG* Solution Name: Name of the solution file without directory portion or .sln extension; Will be empty string if not selected to create solution
		//	  FUCK YOU VISUAL STUDIO... item 5 always contains the project name........
		//	  I guess we need to find another way to do this...
		// 6: Silent: Boolean that indicates whether the wizard should run silently
		// 7: "4.0"
        public void Execute(object Application, int hwndOwner, ref object[] contextParams, ref object[] customParams, ref EnvDTE.wizardResult retval)
        {
			try
			{
				fMain f = new fMain((string)contextParams[1]);
				if( f.ShowDialog() == DialogResult.OK )
				{
					// Set all our member variables based on input:
					//TODO: Organize and validate input
					this.dte = (_DTE)Application;
					this.wz = f.GetWizardData();
					this.solutionName = (string) contextParams[5];
					this.projectName = (string) contextParams[1];
					this.path = (string) contextParams[2];
					this.projectType = (ProjectType) wz.Type.ProjectTemplate;
					this.createNewSolution = (bool)contextParams[4];

					// Make sure main source file isn't null and doesnt have a . (dot):
					if( this.wz.Type.MainLocation.Equals( "" ) )
						this.wz.Type.MainLocation = projectName;
					else if( this.wz.Type.MainLocation.Contains( "." ) )
						this.wz.Type.MainLocation = this.wz.Type.MainLocation.Remove( wz.Type.MainLocation.LastIndexOf( "." ) );

					// Parse project path and solution path from "path"
					//TODO: Can "path" be null/empty?
					this.projectPath = this.path;
					if( this.createNewSolution )
						this.solutionPath = path.Substring(0, this.path.Length - this.projectName.Length - 1);
					else
						this.solutionPath = Path.GetDirectoryName(this.dte.Solution.FullName);

					// Create a Project based on all our input:
					retval = createProject() ? wizardResult.wizardResultSuccess : wizardResult.wizardResultFailure;
				}
				else
				{
					retval = wizardResult.wizardResultCancel;
				}
			}
			catch (System.Exception ex)
			{
				MessageBox.Show("Exception: " + ex.Message, "Error");
				retval = wizardResult.wizardResultBackOut;
			}
        }

		// Main function that does all the work of setting up the project....
		protected bool createProject()
		{
			// Create new solution if we need to....
			if( this.createNewSolution )
			{
                Directory.CreateDirectory(solutionPath);
				this.dte.Solution.Create(this.solutionPath, this.solutionName);
                this.dte.Solution.SaveAs(this.solutionName);
			}

			// Create custom directories:
			Directory.CreateDirectory(solutionPath + "\\BIN");
			Directory.CreateDirectory(solutionPath + "\\Shared");
			Directory.CreateDirectory(solutionPath + "\\Libs");
			Directory.CreateDirectory(solutionPath + "\\Submodules");

			// If .gitignore file doesn't exist, create it: ... This should probably be a function, meh...
			if( !File.Exists(solutionPath + "\\.gitignore") )
			{
				string gitResource = "ProjectWizard.Resources.base..gitignore";
				SortedDictionary<string, Stream> gitIgnore = GetResources(gitResource);

				foreach( var kvp in gitIgnore )
				{
					Stream output = File.OpenWrite(solutionPath + "\\.gitignore");
					if( output != null )
					{
						kvp.Value.CopyTo(output);
						output.Close();
					}
					kvp.Value.Close();
				}
			}

			// Get our includes based on the submodules we've added
			StringBuilder incHeader = new StringBuilder();
			StringBuilder incDirs = new StringBuilder();
			StringBuilder incLibs = new StringBuilder();
			foreach( var item in wz.SubmodulesAr )
			{
				if( item.IncludeStrAr != null )
					foreach( var str in item.IncludeStrAr )
						incHeader.Append( str + "\r\n" );
				if( item.AddlIncludeDirs != null )
					foreach( var str in item.AddlIncludeDirs )
						incDirs.Append( str + ";" );
				if( item.AddlLibDirs != null )
					foreach( var str in item.AddlLibDirs )
						incLibs.Append( str + ";" );
			}
			includes = incHeader.ToString();

			// Copy all the required property sheets into solutiondir/props
			CopyPropertySheets();

			// Create project dir, stage .vcxproj and .filters
			CopyProjFiles();

			// Using EnvDTE, create the VS project...
			EnvDTE.Project project = this.dte.Solution.AddFromFile(this.projectPath + "\\" + this.projectName + ".vcxproj");

			// Copy all project items (source and header files) into project
			AddProjectItems();

			// Let's modify the project now to add the additional include/lib dirs to the configuration:
			try
			{
				if( incDirs.Length > 0 || incLibs.Length > 0 )
				{
					Microsoft.VisualStudio.VCProjectEngine.VCProject proj = (Microsoft.VisualStudio.VCProjectEngine.VCProject)project.Object;
					Microsoft.VisualStudio.VCProjectEngine.IVCCollection configurationsCollection = (Microsoft.VisualStudio.VCProjectEngine.IVCCollection)proj.Configurations;

					foreach( Microsoft.VisualStudio.VCProjectEngine.VCConfiguration configuration in configurationsCollection )
					{
						Microsoft.VisualStudio.VCProjectEngine.IVCCollection toolsCollection = (Microsoft.VisualStudio.VCProjectEngine.IVCCollection)configuration.Tools;
						Microsoft.VisualStudio.VCProjectEngine.VCCLCompilerTool compilerTool = toolsCollection.Item( "VCCLCompilerTool" );
						Microsoft.VisualStudio.VCProjectEngine.VCLinkerTool linkerTool = toolsCollection.Item( "VCLinkerTool" );

						if( incDirs.Length > 0 )
							compilerTool.AdditionalIncludeDirectories += incDirs.ToString();

						if( incLibs.Length > 0 )
							linkerTool.AdditionalLibraryDirectories += incLibs.ToString();
					}
				}
			} catch(System.Exception) {}


			// Initialize git repository
			GitInterop git = new GitInterop( solutionPath );

			// Git exist?
			if( !git.gitExists() )
			{
				MessageBox.Show( "Cannot find Git; No Git functions can be performed.", "Git Error" );
				return false;
			}

			// First let's see if git already exists in the solution, and initialize a repository if not
			bool gitRepo = Directory.Exists( this.solutionPath + "\\.git" );
			if( !gitRepo )
				git.init();

			//Let's add submodules now and other git stuff!
			AddGitSubmodules( git );

			// Save the solution and project and shit
			project.Save();
			this.dte.Solution.SaveAs(this.dte.Solution.FullName);    //this.solutionName );

			// Finally, commit and push to git:
			git.Git_Add( "--all" );
			git.Git_Commit( gitRepo ? "Added Project " + this.projectName : "Initial commit by Project Wizard." );
			if( !gitRepo )
			{
				// Add origin location if provided
				if( wz.Type.OriginLocation != "" )
				{
					git.Remote_Add( wz.Type.OriginLocation );
					git.Git_Push();
				}
				git.Git_CheckoutDevelop();
			}
  
			return true;
		}

		//TODO: We probably need try/catch around all the stream shit in the following functions just in case it isnt writable...
		protected bool CopyPropertySheets()
		{
			string propResource = "ProjectWizard.Resources.props";
			string destination = this.solutionPath + "\\props\\";

			// Create property sheet directory hierarchy:
			Directory.CreateDirectory(destination + "\\internal");

			SortedDictionary<string, Stream> propSheets = GetResources(propResource);
			foreach( var kvp in propSheets )
			{
				StringBuilder propSheet = new StringBuilder(kvp.Key);
				if( propSheet.ToString().StartsWith("internal") )
					propSheet[propSheet.ToString().IndexOf('.')] = '\\';

				Stream output = File.OpenWrite(destination + propSheet.ToString());
				if( output != null )
				{
					kvp.Value.CopyTo(output);
					output.Close();
				}
				kvp.Value.Close();
			}
			return true;
		}

		protected bool CopyProjFiles()
		{
			// Copy everything from ProjectWizard.Resources.proj into new project at $(ProjectDir).
			string projResource = "ProjectWizard.Resources.proj." + ProjectTypeStrings[(int)this.projectType];
			string destination = this.projectPath;

			// Create VS Project directory hierarchy:
			Directory.CreateDirectory(this.projectPath);

			SortedDictionary<string, Stream> projFiles = GetResources(projResource);
			foreach( var kvp in projFiles )
			{
				Stream output = File.OpenWrite(destination + "\\" + this.projectName + kvp.Key.Substring(kvp.Key.IndexOf('.')));
				if( output != null )
				{
					StreamReader reader = new StreamReader(kvp.Value);
					string projFile = ParseData(reader.ReadToEnd());
					reader.Close();

					StreamWriter writer = new StreamWriter(output);
					writer.Write(projFile);
					writer.Close();

					output.Close();
				}
				kvp.Value.Close();
			}
			return true;
		}

		protected bool AddProjectItems()
		{
			// Copy everything from ProjectWizard.Resources.proj into new project at $(ProjectDir).
			string srcResource = "ProjectWizard.Resources.base." + ProjectTypeStrings[(int)this.projectType];
			string destination = this.projectPath;

			SortedDictionary<string, Stream> srcFiles = GetResources(srcResource);
			foreach( var kvp in srcFiles )
			{
				Stream output = File.OpenWrite(destination + "\\" + ParseData(kvp.Key));
				if( output != null )
				{
					// Yeahhh... sooo binary files like the .ico bitmaps dont like to be treated as strings...
					if( !kvp.Key.EndsWith(".ico") )
					{
						StreamReader reader = new StreamReader(kvp.Value);
						string srcFile = ParseData(reader.ReadToEnd());
						reader.Close();

						StreamWriter writer = new StreamWriter(output);
						writer.Write(srcFile);
						writer.Close();
					}
					else
						kvp.Value.CopyTo(output);
					output.Close();
				}
				kvp.Value.Close();
			}
			return true;
		}

		protected bool AddGitSubmodules(GitInterop git)
		{
			try
			{
				// Get the "Submodules" SolutionFolder from the solution explorer:
				Solution2 sol2 = this.dte.Solution as Solution2;
				SolutionFolder submodulesDir = null;

				var projects = sol2.GetEnumerator();
				while( projects.MoveNext() )
				{
					Project proj = (Project)projects.Current;
					if( proj.FullName.Equals( "" ) && proj.Name.Equals( "Submodules" ) )
					{
						submodulesDir = (SolutionFolder)proj.Object;
						break;
					}
				}

				foreach( var item in wz.SubmodulesAr )
				{
					string path = @"./Submodules/" + item.Repo_Name;
					if( !Directory.Exists( solutionPath + "\\Submodules\\" + item.Repo_Name ) )
					{
						// Clone submodule:
						if( git.Submodule_Add( item.Location, path, solutionPath + "\\Submodules\\" + item.Repo_Name ) )
						{
							if( item.AddToSolution )
							{
								// If submodulesDir doesn't exist, let's go ahead and create it
								if( submodulesDir == null )
									submodulesDir = sol2.AddSolutionFolder( "Submodules" ).Object;

								// Add this specific submodule as a nested SolutionFolder and import all its projects:
								string subDir = item.Repo_Name.Remove( item.Repo_Name.IndexOf( "_repo" ) );
								SolutionFolder subProj = submodulesDir.AddSolutionFolder( item.Repo_Name ).Object;
								subProj.AddFromFile( solutionPath + "\\Submodules\\" + item.Repo_Name + "\\" + subDir + "\\" + subDir + ".sln" );
							}
						}
						else
							MessageBox.Show( "Submodule " + item.Name + " failed to clone", "Error Adding Submodule" );
					}
				}
			}
			catch( System.Exception ex )
			{
				MessageBox.Show("Error adding Git submodules: " + ex.Message, "Git Error");
				return false;
			}
			return true;
		}

		// Helper functions:

		// This function will return a filtered list of embedded resources to do work on.
		// The SortedDictionary is to match the relative name to the associated resource data stream.
		private SortedDictionary<string, Stream> GetResources(string filter)
		{
			SortedDictionary<string, Stream> dict = new SortedDictionary<string, Stream>();

			Assembly assembly = Assembly.GetExecutingAssembly();
			foreach( string name in assembly.GetManifestResourceNames() )
			{
				if( !name.StartsWith(filter) )
					continue;

				Stream stream = assembly.GetManifestResourceStream(name);
				if( stream != null )
					dict.Add(name.Substring(filter.Length < name.Length ? filter.Length + 1 : 0), stream);
			}
			return dict;
		}

		// This function will parse a given string and replace all modifiable data with user-input from the wizard.
		private string ParseData(string dataFile)
		{
			// Required fields: Will never be null:
			string retVal = dataFile.Replace("_____FILENAME_____", this.projectName);
			retVal = retVal.Replace("_____SRCFILE_____", wz.Type.MainLocation );

			// Required
			retVal = retVal.Replace( "_____USER_____", wz.Author.Author );
			retVal = retVal.Replace( "_____DATE_____", DateTime.Now.ToString( "M/d/yyyy" ) );
			retVal = retVal.Replace( "_____VERSION_____", wz.Author.Version );

			retVal = wz.Author.Description.Equals( "" ) ? retVal.Replace( " *\r\n * _____DESCRIPTION_____\r\n", "" ) : retVal.Replace( "_____DESCRIPTION_____", wz.Author.Description.Replace( "\r\n", "\r\n * " ) );

			// Header file includes:
			retVal = retVal.Replace( "/*_____USER_INCS_____*/\r\n", includes );

			// vcxproj specific stuff:
			string guid = "<ProjectGuid>";
			string guidEnd = "</ProjectGuid>";
//			string rName = "<RootNamespace>";
//			string rNameEnd = "</RootNamespace>";
			if( retVal.Contains(guid) )
				retVal = retVal.Replace(retVal.Substring(retVal.IndexOf(guid) + guid.Length, retVal.IndexOf(guidEnd) - retVal.IndexOf(guid) - guid.Length), Guid.NewGuid().ToString().ToUpper());
//			if( retVal.Contains(rName) )
//				retVal = retVal.Replace(retVal.Substring(retVal.IndexOf(rName) + rName.Length, retVal.IndexOf(rNameEnd) - retVal.IndexOf(rName) - rName.Length), wz.Author.ToolName);
			return retVal;
		}
    }
}