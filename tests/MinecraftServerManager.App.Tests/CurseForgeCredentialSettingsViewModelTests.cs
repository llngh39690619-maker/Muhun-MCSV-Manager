using System.IO;
using System.Security;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CurseForgeCredentialSettingsViewModelTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EditImportAndDelete_AreImmediateAndNeverDirtyGeneralSettings()
    {
        var store = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(store);
        var openedPaths = new List<string>();
        var synchronizedStates = new List<bool>();
        var credentialSettings = new CurseForgeCredentialSettingsViewModel(
            store,
            importer,
            synchronizedStates.Add,
            path =>
            {
                openedPaths.Add(path);
                return true;
            });
        var generalSettings = CreateGeneralSettings(credentialSettings);

        Assert.False(generalSettings.HasUnsavedChanges);
        Assert.False(credentialSettings.HasCredential);
        Assert.False(credentialSettings.DeleteCredentialCommand.CanExecute(null));

        credentialSettings.OpenCredentialFileCommand.Execute(null);

        Assert.Equal([importer.ImportFilePath], openedPaths);
        Assert.Equal(
            LocalizationService.Current.Get("online.curseForgeCredential.editingFile"),
            credentialSettings.CredentialStatusText);
        Assert.False(generalSettings.HasUnsavedChanges);

        importer.Result = CurseForgeCredentialImportResult.Imported;
        credentialSettings.ImportAndRefresh();

        Assert.True(credentialSettings.HasCredential);
        Assert.True(credentialSettings.DeleteCredentialCommand.CanExecute(null));
        Assert.Equal(
            LocalizationService.Current.Get("online.curseForgeCredential.imported"),
            credentialSettings.CredentialStatusText);
        Assert.Equal([true], synchronizedStates);
        Assert.False(generalSettings.HasUnsavedChanges);

        credentialSettings.DeleteCredentialCommand.Execute(null);

        Assert.False(store.HasCredential);
        Assert.Equal(1, store.DeleteCount);
        Assert.False(credentialSettings.DeleteCredentialCommand.CanExecute(null));
        Assert.Equal(
            LocalizationService.Current.Get("online.curseForgeCredential.deleted"),
            credentialSettings.CredentialStatusText);
        Assert.Equal([true, false], synchronizedStates);
        Assert.False(generalSettings.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(
        (int)CurseForgeCredentialImportResult.TemplateEmpty,
        "online.curseForgeCredential.fileEmpty")]
    [InlineData(
        (int)CurseForgeCredentialImportResult.Invalid,
        "online.curseForgeCredential.fileInvalid")]
    [InlineData(
        (int)CurseForgeCredentialImportResult.Failed,
        "online.curseForgeCredential.importFailed")]
    [InlineData(
        (int)CurseForgeCredentialImportResult.Missing,
        "online.curseForgeCredential.notSaved")]
    public void ActivationRefresh_MapsImportResultWithoutExposingASecret(
        int resultValue,
        string expectedLocalizationKey)
    {
        var result = (CurseForgeCredentialImportResult)resultValue;
        var store = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(store) { Result = result };
        var viewModel = new CurseForgeCredentialSettingsViewModel(store, importer);

        viewModel.ImportAndRefresh();

        Assert.Equal(1, importer.ImportCount);
        Assert.Equal(
            LocalizationService.Current.Get(expectedLocalizationKey),
            viewModel.CredentialStatusText);
        Assert.DoesNotContain("secret", viewModel.CredentialStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationFailures_UpdateOnlyTheStatusAndPreserveCommittedState()
    {
        var store = new FakeCredentialStore(hasCredential: true)
        {
            ThrowOnDelete = true,
        };
        var importer = new FakeCredentialImporter(store)
        {
            ThrowOnPrepare = true,
        };
        var synchronizedStates = new List<bool>();
        var viewModel = new CurseForgeCredentialSettingsViewModel(
            store,
            importer,
            synchronizedStates.Add,
            static _ => true);

        viewModel.OpenCredentialFileCommand.Execute(null);

        Assert.Equal(
            LocalizationService.Current.Get("online.curseForgeCredential.openFileFailed"),
            viewModel.CredentialStatusText);
        Assert.True(viewModel.HasCredential);

        viewModel.DeleteCredentialCommand.Execute(null);

        Assert.True(viewModel.HasCredential);
        Assert.Equal(
            LocalizationService.Current.Get("online.curseForgeCredential.deleteFailed"),
            viewModel.CredentialStatusText);
        Assert.Empty(synchronizedStates);
    }

    [Fact]
    public void SettingsCard_HasNoSecretInputAndActivationRefreshesTheImportedFile()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Dialogs",
            "GeneralSettingsDialog.xaml"));
        var card = Assert.Single(
            document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute(Xaml + "Name") ==
                       "CurseForgeCredentialSettingsCard");

        Assert.Equal("OnActivated", (string?)document.Root?.Attribute("Activated"));
        Assert.DoesNotContain(card.Descendants(), element =>
            element.Name == Presentation + "TextBox" ||
            element.Name == Presentation + "PasswordBox");
        Assert.Contains(card.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") ==
            "{Binding CurseForgeCredentialSettings.CredentialStatusText}");
        Assert.Contains(card.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") ==
            "{Binding CurseForgeCredentialSettings.OpenCredentialFileCommand}");
        Assert.Contains(card.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") ==
            "{Binding CurseForgeCredentialSettings.DeleteCredentialCommand}");

        var codeBehind = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Dialogs",
            "GeneralSettingsDialog.xaml.cs"));
        Assert.Contains(
            "CurseForgeCredentialSettings?.ImportAndRefresh()",
            codeBehind,
            StringComparison.Ordinal);
    }

    private static GeneralSettingsViewModel CreateGeneralSettings(
        CurseForgeCredentialSettingsViewModel credentialSettings)
        => new(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            static (_, _, _) => Task.CompletedTask,
            curseForgeCredentialSettings: credentialSettings);

    private sealed class FakeCredentialStore(bool hasCredential) : ICurseForgeCredentialStore
    {
        public bool HasCredential { get; set; } = hasCredential;

        public bool ThrowOnDelete { get; init; }

        public int DeleteCount { get; private set; }

        public SecureString? AcquireReadOnly() => null;

        public void Save(SecureString credential)
            => HasCredential = true;

        public bool Delete()
        {
            DeleteCount++;
            if (ThrowOnDelete)
            {
                throw new IOException("Synthetic delete failure containing no credential.");
            }

            var deleted = HasCredential;
            HasCredential = false;
            return deleted;
        }
    }

    private sealed class FakeCredentialImporter(FakeCredentialStore store) :
        ICurseForgeCredentialFileImportService
    {
        public string ImportFilePath { get; } =
            Path.Combine(Path.GetTempPath(), "x-mcsv-curseforge-api-key.import.txt");

        public CurseForgeCredentialImportResult Result { get; set; } =
            CurseForgeCredentialImportResult.Missing;

        public bool ThrowOnPrepare { get; init; }

        public int ImportCount { get; private set; }

        public string PrepareEditableFile()
        {
            if (ThrowOnPrepare)
            {
                throw new IOException("Synthetic open failure containing no credential.");
            }

            return ImportFilePath;
        }

        public CurseForgeCredentialImportResult ImportIfPresent()
        {
            ImportCount++;
            if (Result == CurseForgeCredentialImportResult.Imported)
            {
                store.HasCredential = true;
            }

            return Result;
        }
    }
}
