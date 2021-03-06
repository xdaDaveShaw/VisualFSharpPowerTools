﻿using System.Diagnostics;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System.Collections.ObjectModel;
using System;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE80;
using EnvDTE;
using FSharp.Editing.VisualStudio.Navigation;
using FSharp.Editing.VisualStudio.ProjectSystem;
using FSharp.Editing.VisualStudio;

namespace FSharpVSPowerTools
{
    [Export(typeof(DotNetReferenceSourceProvider))]
    public class DotNetReferenceSourceProvider : ReferenceSourceProvider
    {
        public DotNetReferenceSourceProvider() : base("http://referencesource.microsoft.com") { }
    }

    [Export(typeof(IVsTextViewCreationListener))]
    [Export(typeof(IWpfTextViewConnectionListener))]
    [ContentType("F#")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    public class GoToDefinitionFilterProvider : IVsTextViewCreationListener, IWpfTextViewConnectionListener, IDisposable
    {
        private readonly IVsEditorAdaptersFactoryService _editorFactory;
        private readonly ITextDocumentFactoryService _textDocumentFactoryService;
        private readonly VSLanguageService _fsharpVsLanguageService;
        private readonly IEditorOptionsFactoryService _editorOptionsFactory;
        private readonly ProjectFactory _projectFactory;
        private readonly ReferenceSourceProvider _referenceSourceProvider;
        private readonly NavigateToMetadataService _navigationService;
        private readonly System.IServiceProvider _serviceProvider;
        private readonly SolutionEvents _solutionEvents;

        private static readonly Type serviceType = typeof(GoToDefinitionFilterProvider);
        
        [ImportingConstructor]
        public GoToDefinitionFilterProvider(
            [Import(typeof(SVsServiceProvider))] System.IServiceProvider serviceProvider,
            IVsEditorAdaptersFactoryService editorFactory,
            IEditorOptionsFactoryService editorOptionsFactory,
            ITextDocumentFactoryService textDocumentFactoryService,
            [Import(typeof(DotNetReferenceSourceProvider))] ReferenceSourceProvider referenceSourceProvider,
            NavigateToMetadataService metadataService,
            VSLanguageService languageService,
            ProjectFactory projectFactory)
        {
            _serviceProvider = serviceProvider;
            _editorFactory = editorFactory;
            _editorOptionsFactory = editorOptionsFactory;
            _textDocumentFactoryService = textDocumentFactoryService;
            _referenceSourceProvider = referenceSourceProvider;
            _navigationService = metadataService;
            _fsharpVsLanguageService = languageService;
            _projectFactory = projectFactory;

            var dte = serviceProvider.GetService(typeof(SDTE)) as DTE;
            var events = dte.Events as Events2;
            if (events != null)
            {
                _solutionEvents = events.SolutionEvents;
                _solutionEvents.AfterClosing += Cleanup;
            }
        }

        private void Cleanup()
        {
            NavigateToMetadataService.ClearXmlDocCache();
        }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var textView = _editorFactory.GetWpfTextView(textViewAdapter);
            if (textView == null) return;
            Register(textViewAdapter, textView, fireNavigationEvent: false);
        }

        internal GoToDefinitionFilter RegisterCommandFilter(IWpfTextView textView, bool fireNavigationEvent)
        {
            var textViewAdapter = _editorFactory.GetViewAdapter(textView);
            return textViewAdapter == null ? null : Register(textViewAdapter, textView, fireNavigationEvent);
        }

        private GoToDefinitionFilter Register(IVsTextView textViewAdapter, IWpfTextView textView, bool fireNavigationEvent)
        {
            var generalOptions = Setting.getGeneralOptions(_serviceProvider);
            if (generalOptions == null || (!generalOptions.GoToMetadataEnabled && !generalOptions.GoToSymbolSourceEnabled)) return null;
            // Favor Navigate to Source feature over Go to Metadata
            var preference = generalOptions.GoToSymbolSourceEnabled
                                ? (generalOptions.GoToMetadataEnabled ? NavigationPreference.SymbolSourceOrMetadata : NavigationPreference.SymbolSource) 
                                : NavigationPreference.Metadata;
            ITextDocument doc;
            if (_textDocumentFactoryService.TryGetTextDocument(textView.TextBuffer, out doc))
            {
                var commandFilter = new GoToDefinitionFilter(doc, textView, _fsharpVsLanguageService,
                                                             _serviceProvider, _projectFactory, _referenceSourceProvider,
                                                             _navigationService, preference, fireNavigationEvent);
                if (!_referenceSourceProvider.IsActivated && generalOptions.GoToSymbolSourceEnabled)
                    _referenceSourceProvider.Activate();
                textView.Properties.AddProperty(serviceType, commandFilter);
                Utils.AddCommandFilter(textViewAdapter, commandFilter);
                return commandFilter;
            }
            return null;
        }

        public void SubjectBuffersConnected(IWpfTextView textView, ConnectionReason reason, Collection<ITextBuffer> subjectBuffers)
        {
        }

        public void SubjectBuffersDisconnected(IWpfTextView textView, ConnectionReason reason, Collection<ITextBuffer> subjectBuffers)
        {
            if (reason != ConnectionReason.TextViewLifetime) return;

            GoToDefinitionFilter commandFilter;
            if (textView.Properties.TryGetProperty(serviceType, out commandFilter))
            {
                var textViewAdapter = _editorFactory.GetViewAdapter(textView);
                int hr = textViewAdapter.RemoveCommandFilter(commandFilter);
                Debug.Assert(hr == VSConstants.S_OK, "Should be able to unwire the command.");
                (commandFilter as IDisposable).Dispose();
            }
        }

        public void Dispose()
        {
            if (_solutionEvents != null)
                _solutionEvents.AfterClosing -= Cleanup;
            (_referenceSourceProvider as IDisposable).Dispose();
        }
    }
}