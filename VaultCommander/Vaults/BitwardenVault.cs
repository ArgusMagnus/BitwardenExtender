﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace VaultCommander.Vaults;

sealed partial class BitwardenVault : IVault, IAsyncDisposable
{
    readonly CliClient _cli;
    EncryptedString? _savedPw;

    public string VaultName => "Bitwarden";
    public string UriScheme => "bwext";

    public string UriFieldName => nameof(VaultCommander);

    BitwardenVault(string dataDirectoryRoot) => _cli = new(Path.Combine(dataDirectoryRoot, VaultName, "bw.exe"));

    public async Task<StatusDto?> GetStatus()
    {
        if (!File.Exists(_cli.ExePath))
            return new(null, null, null, null, Status.Unauthenticated);
        if (_cli.IsAttachedToApiServer)
            return await (await _cli.GetApiClient()).GetStatus();
        return await _cli.GetStatus();
    }

    async Task UpdateCli()
    {
        if (await _cli.GetUpdateUri() is not Uri uri)
            return;

        using var progressBox = await ProgressBox.Show();
        progressBox.StepText = "0 / 1";
        progressBox.DetailText = "Bitwarden CLI herunterladen...";

        void OnProgress(double progress)
        {
            progressBox.DetailProgress = progress * 2;
            if (progress is 0.5)
            {
                progressBox.StepText = "1 / 1";
                progressBox.StepProgress = 0.5;
                progressBox.DetailText = "Bitwarden CLI installieren...";
            }
        }

        if (_cli.TryAttachToApiServer())
            _cli.KillServer();
        await Utils.DownloadAndExpandZipArchive(uri,
            name => string.Equals(".exe", Path.GetExtension(name), StringComparison.OrdinalIgnoreCase) ? _cli.ExePath : null,
            OnProgress);

        progressBox.StepProgress = 1;
    }

    public async Task<StatusDto?> Initialize()
    {
        if (File.Exists(_cli.ExePath))
        {
            await UpdateCli();
            if (await _cli.StartApiServer(null))
                return await GetStatus();
        }
        return new(null, null, null, null, Status.Unauthenticated);
    }

    public async Task<StatusDto?> Login()
    {
        if (!File.Exists(_cli.ExePath))
            await UpdateCli();

        _savedPw = null;

        while (true)
        {
            var cred = PasswordDialog.Show(Application.Current.MainWindow, null);
            if (cred == default)
                break;
            if (string.IsNullOrEmpty(cred.UserEmail) || cred.Password is null)
                continue;
            var sessionToken = await _cli.Login(cred.UserEmail, cred.Password);
            if (string.IsNullOrEmpty(sessionToken))
                continue;
            _savedPw = cred.Password;
            await _cli.StartApiServer(sessionToken);
            break;
        }

        return await GetStatus();
    }

    public async Task Sync() => await UseApi(api => api.Sync());

    public async Task<ItemTemplate?> UpdateUris(Guid guid)
    {
        using var progressBox = await ProgressBox.Show();
        progressBox.DetailText = "Einträge aktualisieren...";
        var (item, count) = await UseApi(async api =>
        {
            await api.Sync();
            ItemTemplate? item = null;
            var count = 0;
            var itemsDto = await api.GetItems();
            if (itemsDto?.Success is not true || itemsDto.Data?.Data is null)
                return (item, count);

            var prefix = $"{UriScheme}:";
            foreach (var (data, idx) in itemsDto.Data.Data.Select((x,i) => (x,i)))
            {
                progressBox.DetailProgress = (idx + 1.0) / itemsDto.Data.Data.Count;
                var element = data.Fields.Select((x, i) => (x, i)).FirstOrDefault(x => x.x.Name == UriFieldName);
                if (data.Login is not null)
                {
                    if (element != default)
                        data.Fields.RemoveAt(element.i);
                    var uri = data.Login.Uris.Select((x, i) => (x, i)).FirstOrDefault(x => x.x.Uri?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) is true);
                    if (Guid.TryParse(uri.x?.Uri?.Substring(prefix.Length), out var tmp) && tmp == data.Id)
                        continue;
                    var newUri = new ItemUri { Uri = $"{prefix}{data.Id}", Match = UriMatchType.Never };
                    if (uri == default)
                        data.Login.Uris.Add(newUri);
                    else
                        data.Login.Uris[uri.i] = newUri;
                }
                else
                {
                    if (element.x?.Value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) is true && Guid.TryParse(element.x.Value.Substring(prefix.Length), out var tmp) && tmp == data.Id)
                        continue;
                    var newField = new Field { Name = UriFieldName, Value = $"{prefix}{data.Id}", Type = FieldType.Text };
                    if (element == default)
                        data.Fields.Insert(0, newField);
                    else
                        data.Fields[element.i] = newField;
                }

                await api.PutItem(data);
                if (data.Id == guid)
                    item = data;
                count++;
            }

            return (item, count);
        });
        return item;
    }

    public async Task Logout()
    {
        _cli.KillServer();
        await _cli.Logout();
        if (Directory.Exists(_cli.AppDataDir))
        {
            try { Directory.Delete(_cli.AppDataDir, true); }
            catch
            {
                foreach (var file in Directory.EnumerateFiles(_cli.AppDataDir))
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
        }
    }

    async Task<T> UseApi<T>(Func<ApiClient, Task<T>> func)
    {
        var api = await _cli.GetApiClient();
        T result;
        try
        {
            var status = await api.GetStatus();
            if (status?.Status is Status.Locked)
            {
                _savedPw = null;
                while (_savedPw is null || !await api.Unlock(_savedPw))
                {
                    _savedPw = PasswordDialog.Show(Application.Current.MainWindow, status.UserEmail).Password;
                }
            }
            result = await func(api);
        }
        finally
        {
            if (!Debugger.IsAttached)
                await api.Lock();
        }
        return result;
    }

    Task UseApi(Func<ApiClient, Task> func) => UseApi(async api => { await func(api); return true; });

    public async Task<ItemTemplate?> GetItem(Guid guid)
    {
        var response = await UseApi(api => api.GetItem(guid));
        return response?.Success is true ? response.Data : null;
    }

    public async Task<string?> GetTotp(Guid guid) => (await UseApi(api => api.GetTotp(guid)))?.Data?.Data;

    public async ValueTask DisposeAsync()
    {
        if (_cli.IsAttachedToApiServer)
        {
            try { await (await _cli.GetApiClient()).Lock(); }
            catch { }
        }
        _cli.Dispose();
    }

    sealed class Factory : IVaultFactory
    {
        public IVault CreateVault(string dataDirectoryRoot) => new BitwardenVault(dataDirectoryRoot);
    }
}