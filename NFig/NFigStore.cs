﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NFig.Encryption;
using NFig.Logging;

namespace NFig
{
    public abstract class NFigStore<TSettings, TSubApp, TTier, TDataCenter>
        where TSettings : class, INFigSettings<TSubApp, TTier, TDataCenter>, new()
        where TSubApp : struct
        where TTier : struct
        where TDataCenter : struct
    {
        /// <summary>
        /// The Commit value which should be used when no overrides have ever been set for the application.
        /// </summary>
        public const string INITIAL_COMMIT = "00000000-0000-0000-0000-000000000000";

        /// <summary>
        /// The Commit value which should be used when no overrides have ever been set for the application.
        /// </summary>
        public string InitialCommit => INITIAL_COMMIT;

        public delegate void SettingsUpdateDelegate(Exception ex, TSettings settings, NFigStore<TSettings, TSubApp, TTier, TDataCenter> store);

        class TierDataCenterCallback
        {
            public SettingsUpdateDelegate Callback { get; }
            public string LastNotifiedCommit { get; set; } = "NONE";

            public TierDataCenterCallback(SettingsUpdateDelegate callback)
            {
                Callback = callback;
            }
        }

        readonly SettingsFactory<TSettings, TSubApp, TTier, TDataCenter> _factory;

        readonly object _callbacksLock = new object();
        readonly Dictionary<string, TierDataCenterCallback[]> _callbacksByApp = new Dictionary<string, TierDataCenterCallback[]>();

        readonly object _dataCacheLock = new object();
        readonly Dictionary<string, AppSnapshot<TSubApp, TTier, TDataCenter>> _dataCache = new Dictionary<string, AppSnapshot<TSubApp, TTier, TDataCenter>>();

        Timer _pollingTimer;

        public string ApplicationName => _factory.ApplicationName;
        public TTier Tier => _factory.Tier;
        public TDataCenter DataCenter => _factory.DataCenter;

        public SettingsLogger<TTier, TDataCenter> Logger { get; }

        public int PollingInterval { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="appName">The name of the current application.</param>
        /// <param name="tier">Tier the application is running on (cannot be the default "Any" value).</param>
        /// <param name="dataCenter">DataCenter the application is running in (cannot be the default "Any" value).</param>
        /// <param name="logger">The logger which events will be sent to.</param>
        /// <param name="encryptor">
        /// Object which will provide encryption/decryption for encrypted settings. Only required if there are encrypted settings in use.
        /// </param>
        /// <param name="additionalDefaultConverters">
        /// Allows you to specify additional (or replacement) default converters for types. Each key/value pair must be in the form of
        /// (typeof(T), <see cref="ISettingConverter{T}"/>).
        /// </param>
        /// <param name="pollingInterval">The interval, in seconds, to poll for override changes. Use 0 to disable polling.</param>
        protected NFigStore(
            string appName,
            TTier tier,
            TDataCenter dataCenter,
            SettingsLogger<TTier, TDataCenter> logger,
            ISettingEncryptor encryptor,
            Dictionary<Type, object> additionalDefaultConverters,
            int pollingInterval = 60)
        {
            Logger = logger;
            PollingInterval = pollingInterval;
            _factory = new SettingsFactory<TSettings, TSubApp, TTier, TDataCenter>(appName, tier, dataCenter, encryptor, additionalDefaultConverters);

            if (Compare.IsDefault(tier))
                throw new ArgumentOutOfRangeException(nameof(tier), $"Tier cannot be the default enum value ({tier}) because it represents the \"Any\" tier.");

            if (Compare.IsDefault(dataCenter))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dataCenter),
                    $"DataCenter cannot be the default enum value ({dataCenter}) because it represents the \"Any\" data center.");
            }

            if (pollingInterval > 0)
                _pollingTimer = new Timer(PollForChanges, null, pollingInterval * 1000, pollingInterval * 1000);
        }

// === Public Non-Virtual Methods ===

        /// <summary>
        /// Sets an override for the specified application, setting name, tier, and data center combination. If an existing override shares that exact
        /// combination, it will be replaced.
        /// </summary>
        /// <param name="settingName">Name of the setting.</param>
        /// <param name="value">The string-value of the setting. If the setting is an encrypted setting, this value must be pre-encrypted.</param>
        /// <param name="dataCenter">Data center which the override should be applicable to.</param>
        /// <param name="user">The user who is setting the override (used for logging purposes). This can be null.</param>
        /// <param name="subApp">(optional) The sub app which the override should apply to.</param>
        /// <param name="commit">(optional) If non-null, the override will only be applied if this is the current commit ID.</param>
        /// <returns>
        /// A snapshot of the state immediately after the override is applied. If the override is not applied because the current commit didn't match the
        /// commit parameter, the return value will be null.
        /// </returns>
        public async Task<AppSnapshot<TTier, TDataCenter>> SetOverrideAsync(
            string settingName,
            string value,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp = default(TSubApp),
            string commit = null)
        {
            var snapshot = await SetOverrideAsyncImpl(settingName, value, dataCenter, user, subApp, commit);

            if (snapshot != null)
                LogAndNotifyChange(snapshot);

            return snapshot;
        }

        /// <summary>
        /// Synchronous version of SetOverrideAsync. You should use the async version if possible because it may have a lower risk of deadlocking in some
        /// circumstances.
        /// </summary>
        /// <param name="settingName">Name of the setting.</param>
        /// <param name="value">The string-value of the setting. If the setting is an encrypted setting, this value must be pre-encrypted.</param>
        /// <param name="dataCenter">Data center which the override should be applicable to.</param>
        /// <param name="user">The user who is setting the override (used for logging purposes). This can be null.</param>
        /// <param name="subApp">(optional) The sub app which the override should apply to.</param>
        /// <param name="commit">(optional) If non-null, the override will only be applied if this is the current commit ID.</param>
        /// <returns>
        /// A snapshot of the state immediately after the override is applied. If the override is not applied because the current commit didn't match the
        /// commit parameter, the return value will be null.
        /// </returns>
        public AppSnapshot<TTier, TDataCenter> SetOverride(
            string settingName,
            string value,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp = default(TSubApp),
            string commit = null)
        {
            var snapshot = SetOverrideImpl(settingName, value, dataCenter, user, subApp, commit);

            if (snapshot != null)
                LogAndNotifyChange(snapshot);

            return snapshot;
        }

        /// <summary>
        /// Clears an override with the specified application, setting name, tier, and data center combination. Even if the override does not exist, this
        /// operation may result in a change of the current commit, depending on the store's implementation.
        /// </summary>
        /// <param name="settingName">Name of the setting.</param>
        /// <param name="dataCenter">Data center which the override is applied to.</param>
        /// <param name="user">The user who is clearing the override (used for logging purposes). This can be null.</param>
        /// <param name="subApp">The sub app which the override is applied to.</param>
        /// <param name="commit">(optional) If non-null, the override will only be cleared if this is the current commit ID.</param>
        /// <returns>
        /// A snapshot of the state immediately after the override is cleared. If the override is not applied, either because it didn't exist, or because the 
        /// current commit didn't match the commit parameter, the return value will be null.
        /// </returns>
        public async Task<AppSnapshot<TTier, TDataCenter>> ClearOverrideAsync(
            string settingName,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp = default(TSubApp),
            string commit = null)
        {
            var snapshot = await ClearOverrideAsyncImpl(settingName, dataCenter, user, subApp, commit);

            if (snapshot != null)
                LogAndNotifyChange(snapshot);

            return snapshot;
        }

        /// <summary>
        /// Synchronous version of ClearOverrideAsync. You should use the async version if possible because it may have a lower risk of deadlocking in some circumstances.
        /// </summary>
        /// <param name="settingName">Name of the setting.</param>
        /// <param name="dataCenter">Data center which the override is applied to.</param>
        /// <param name="user">The user who is clearing the override (used for logging purposes). This can be null.</param>
        /// <param name="subApp">The sub app which the override is applied to.</param>
        /// <param name="commit">(optional) If non-null, the override will only be cleared if this is the current commit ID.</param>
        /// <returns>
        /// A snapshot of the state immediately after the override is cleared. If the override is not applied, either because it didn't exist, or because the 
        /// current commit didn't match the commit parameter, the return value will be null.
        /// </returns>
        public AppSnapshot<TTier, TDataCenter> ClearOverride(
            string settingName,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp = default(TSubApp),
            string commit = null)
        {
            var snapshot = ClearOverrideImpl(settingName, dataCenter, user, subApp, commit);

            if (snapshot != null)
                LogAndNotifyChange(snapshot);

            return snapshot;
        }

        /// <summary>
        /// Gets a dictionary of hydrated TSettings objects keyed by sub app.
        /// </summary>
        public async Task<Dictionary<TSubApp, TSettings>> GetAppSettingsAsync()
        {
            var data = await GetAppSnapshotAsync().ConfigureAwait(false);

            TSettings settings;
            var ex = GetSettingsObjectFromData(data, out settings);
            if (ex != null)
                throw ex;

            return settings;
        }

        /// <summary>
        /// Synchronous version of GetAppSettingsAsync.
        /// </summary>
        public Dictionary<TSubApp, TSettings> GetAppSettings()
        {
            var data = GetAppSnapshot();

            TSettings settings;
            var ex = GetSettingsObjectFromData(data, out settings);
            if (ex != null)
                throw ex;

            return settings;
        }

        /// <summary>
        /// Returns a SettingInfo object for each setting. The SettingInfo contains meta data about the setting, as well as lists of the default values and current overrides.
        /// Default values which are not applicable to the current tier are not included.
        /// </summary>
        public async Task<SettingInfo<TSubApp, TTier, TDataCenter>[]> GetAllSettingInfosAsync()
        {
            var data = await GetAppSnapshotAsync().ConfigureAwait(false);
            return _factory.GetAllSettingInfos(data.Overrides);
        }

        /// <summary>
        /// Synchronous version of GetAllSettingInfosAsync.
        /// </summary>
        public SettingInfo<TSubApp, TTier, TDataCenter>[] GetAllSettingInfos()
        {
            var data = GetAppSnapshot();
            return _factory.GetAllSettingInfos(data.Overrides);
        }

        /// <summary>
        /// Returns true if the commit ID on the settings object matches the most current commit ID.
        /// </summary>
        public async Task<bool> IsCurrentAsync(TSettings settings)
        {
            var commit = await GetCurrentCommitAsync().ConfigureAwait(false);
            return commit == settings.Commit;
        }

        /// <summary>
        /// Synchronous version of IsCurrentAsync.
        /// </summary>
        public bool IsCurrent(TSettings settings)
        {
            if (settings.ApplicationName != ApplicationName)
            {
                var ex = new NFigException("Cannot evaluate IsCurrent() for settings object. ApplicationName does not match the store.");
                ex.Data["NFigStore.ApplicationName"] = ApplicationName;
                ex.Data["settings.ApplicationName"] = settings.ApplicationName;
                throw ex;
            }

            var commit = GetCurrentCommit();
            return commit == settings.Commit;
        }

        /// <summary>
        /// Returns true if a setting by that name exists. For nested settings, use dot notation (i.e. "Some.Setting").
        /// </summary>
        public bool SettingExists(string settingName)
        {
            return _factory.SettingExists(settingName);
        }

        /// <summary>
        /// Returns true if the setting is an encrypted setting.
        /// </summary>
        public bool IsEncrypted(string settingName)
        {
            return _factory.IsEncrypted(settingName);
        }

        /// <summary>
        /// Returns a string encrypted with the ISettingEncryptor provided when the NFigStore was initialized.
        /// If no encryptor was provided, this method will throw an exception.
        /// Null values are not encrypted, and are simply returned as null.
        /// </summary>
        public string Encrypt(string plainText)
        {
            if (!_factory.HasEncryptor)
                throw new NFigException("No ISettingEncryptor was provided when the NFigStore was initialized");

            return _factory.Encrypt(plainText);
        }

        /// <summary>
        /// Decrypts the string using the ISettingEncryptor provided when the NFigStore was initialized.
        /// If no encryptor was provided, this method will throw an exception.
        /// Null are considered to be unencrypted to begin with, and will result in a null return value.
        /// </summary>
        public string Decrypt(string encrypted)
        {
            if (!_factory.HasEncryptor)
                throw new NFigException("No ISettingEncryptor was provided when the NFigStore was initialized");

            return _factory.Decrypt(encrypted);
        }

        /// <summary>
        /// Returns the property type 
        /// </summary>
        public Type GetSettingType(string settingName)
        {
            return _factory.GetSettingType(settingName);
        }

        /// <summary>
        /// Extracts the value of a setting by name from an existing TSettings object.
        /// </summary>
        public object GetSettingValue(TSettings obj, string settingName)
        {
            return _factory.GetSettingValue(obj, settingName);
        }

        /// <summary>
        /// Extracts the value of a setting by name from an existing TSettings object.
        /// </summary>
        public TValue GetSettingValue<TValue>(TSettings obj, string settingName)
        {
            return _factory.GetSettingValue<TValue>(obj, settingName);
        }

        /// <summary>
        /// Returns true if the given string can be converted into a valid value 
        /// </summary>
        public bool IsValidStringForSetting(string settingName, string value)
        {
            return _factory.IsValidStringForSetting(settingName, value);
        }

        public async Task<AppSnapshot<TSubApp, TTier, TDataCenter>> GetAppSnapshotAsync()
        {
            AppSnapshot<TSubApp, TTier, TDataCenter> snapshot;

            // check cache first
            bool cacheExisted;
            lock (_dataCacheLock)
            {
                cacheExisted = _dataCache.TryGetValue(ApplicationName, out snapshot);
            }

            if (cacheExisted)
            {
                var commit = await GetCurrentCommitAsync().ConfigureAwait(false);
                if (snapshot.Commit == commit)
                    return snapshot;
            }

            snapshot = await GetAppSnapshotNoCacheAsync();

            lock (_dataCacheLock)
            {
                _dataCache[ApplicationName] = snapshot;
            }

            if (!cacheExisted)
                await DeleteOrphanedOverridesAsync(snapshot);

            return snapshot;
        }

        public AppSnapshot<TSubApp, TTier, TDataCenter> GetAppSnapshot()
        {
            AppSnapshot<TSubApp, TTier, TDataCenter> snapshot;

            // check cache first
            bool cacheExisted;
            lock (_dataCacheLock)
            {
                cacheExisted = _dataCache.TryGetValue(ApplicationName, out snapshot);
            }

            if (cacheExisted)
            {
                var commit = GetCurrentCommit();
                if (snapshot.Commit == commit)
                    return snapshot;
            }

            snapshot = GetAppSnapshotNoCache();

            lock (_dataCacheLock)
            {
                _dataCache[ApplicationName] = snapshot;
            }

            if (!cacheExisted)
                DeleteOrphanedOverrides(snapshot);

            return snapshot;
        }

        public async Task<AppSnapshot<TSubApp, TTier, TDataCenter>> RestoreSnapshotAsync(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot, string user)
        {
            if (snapshot.ApplicationName != ApplicationName)
            {
                var ex = new NFigException("Cannot restore snapshot. ApplicationName does not match that of the store.");
                ex.Data["NFigStore.ApplicationName"] = ApplicationName;
                ex.Data["snapshot.ApplicationName"] = snapshot.ApplicationName;
            }

            var newSnapshot = await RestoreSnapshotAsyncImpl(snapshot, user);
            LogAndNotifyChange(newSnapshot);
            return newSnapshot;
        }

        public AppSnapshot<TSubApp, TTier, TDataCenter> RestoreSnapshot(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot, string user)
        {
            if (snapshot.ApplicationName != ApplicationName)
            {
                var ex = new NFigException("Cannot restore snapshot. ApplicationName does not match that of the store.");
                ex.Data["NFigStore.ApplicationName"] = ApplicationName;
                ex.Data["snapshot.ApplicationName"] = snapshot.ApplicationName;
            }

            var newSnapshot = RestoreSnapshotImpl(snapshot, user);
            LogAndNotifyChange(newSnapshot);
            return newSnapshot;
        }

// === Virtual Methods ===

        /// <summary>
        /// Returns the most current Commit ID for a given application. The commit may be null if there are no overrides set.
        /// </summary>
        public abstract Task<string> GetCurrentCommitAsync();

        /// <summary>
        /// Synchronous version of GetCurrentCommitAsync. You should use the async version if possible because it has a lower risk of deadlocking in some circumstances.
        /// </summary>
        public virtual string GetCurrentCommit()
        {
            return Task.Run(async () => await GetCurrentCommitAsync()).Result;
        }

        protected abstract Task<AppSnapshot<TSubApp, TTier, TDataCenter>> RestoreSnapshotAsyncImpl(
            AppSnapshot<TSubApp, TTier, TDataCenter> snapshot,
            string user);

        protected virtual AppSnapshot<TSubApp, TTier, TDataCenter> RestoreSnapshotImpl(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot, string user)
        {
            return Task.Run(async () => await RestoreSnapshotAsyncImpl(snapshot, user)).Result;
        }

        protected abstract Task<AppSnapshot<TSubApp, TTier, TDataCenter>> SetOverrideAsyncImpl(
            string settingName,
            string value,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp,
            string commit);

        protected virtual AppSnapshot<TSubApp, TTier, TDataCenter> SetOverrideImpl(
            string settingName,
            string value,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp,
            string commit)
        {
            return Task.Run(async () => await SetOverrideAsyncImpl(settingName, value, dataCenter, user, subApp, commit)).Result;
        }

        protected abstract Task<AppSnapshot<TSubApp, TTier, TDataCenter>> ClearOverrideAsyncImpl(
            string settingName,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp,
            string commit);
        
        protected virtual AppSnapshot<TSubApp, TTier, TDataCenter> ClearOverrideImpl(
            string settingName,
            TDataCenter dataCenter,
            string user,
            TSubApp subApp,
            string commit)
        {
            return Task.Run(async () => await ClearOverrideAsyncImpl(settingName, dataCenter, user, subApp, commit)).Result;
        }

        protected abstract Task PushUpdateNotificationAsync();

        protected virtual void PushUpdateNotification()
        {
            Task.Run(async () => { await PushUpdateNotificationAsync(); }).Wait();
        }

        protected abstract Task<AppSnapshot<TSubApp, TTier, TDataCenter>> GetAppSnapshotNoCacheAsync();

        protected virtual AppSnapshot<TSubApp, TTier, TDataCenter> GetAppSnapshotNoCache()
        {
            return Task.Run(async () => await GetAppSnapshotNoCacheAsync()).Result;
        }

        protected abstract Task DeleteOrphanedOverridesAsync(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot);

        protected virtual void DeleteOrphanedOverrides(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot)
        {
            Task.Run(async () => await DeleteOrphanedOverridesAsync(snapshot)).Wait();
        }

        protected virtual void OnSubscribe(SettingsUpdateDelegate callback)
        {
        }

// === Subscriptions ===

        public void SubscribeToAppSettings(SettingsUpdateDelegate callback)
        {
            TierDataCenterCallback[] callbacks;
            lock (_callbacksLock)
            {
                var info = new TierDataCenterCallback(callback);
                if (_callbacksByApp.TryGetValue(ApplicationName, out callbacks))
                {
                    foreach (var c in callbacks)
                    {
                        if (c.Callback == callback)
                            return; // callback already exists, no need to add it again
                    }

                    var oldCallbacks = callbacks;
                    callbacks = new TierDataCenterCallback[oldCallbacks.Length + 1];
                    Array.Copy(oldCallbacks, callbacks, oldCallbacks.Length);
                    callbacks[oldCallbacks.Length] = info;

                    _callbacksByApp[ApplicationName] = callbacks;
                }
                else
                {
                    callbacks = new[] { info };
                    _callbacksByApp[ApplicationName] = callbacks;
                }
            }

            OnSubscribe(callback);
            ReloadAndNotifyCallback(callbacks);
        }

        void ReloadAndNotifyCallback(TierDataCenterCallback[] callbacks)
        {
            if (callbacks == null || callbacks.Length == 0)
                return;

            Exception ex = null;
            AppSnapshot<TSubApp, TTier, TDataCenter> snapshot = null;
            try
            {
                snapshot = GetAppSnapshot();
            }
            catch (Exception e)
            {
                ex = e;
            }

            foreach (var c in callbacks)
            {
                if (c.Callback == null)
                    continue;

                if (snapshot != null && snapshot.Commit == c.LastNotifiedCommit)
                    continue;

                TSettings settings = null;
                Exception inner = null;
                if (ex == null)
                {
                    try
                    {
                        ex = GetSettingsObjectFromData(snapshot, out settings);
                        c.LastNotifiedCommit = snapshot.Commit;
                    }
                    catch (Exception e)
                    {
                        inner = e;
                    }
                }

                c.Callback(ex ?? inner, settings, this);
            }
        }

        /// <summary>
        /// Unsubscribes from app settings updates.
        /// Note that there is a potential race condition if you unsibscribe while an update is in progress, the prior callback may still get called.
        /// </summary>
        /// <param name="callback">(optional) If null, any callback will be removed. If specified, a current callback will only be removed if it is equal to this param.</param>
        /// <returns>The number of callbacks removed.</returns>
        public int UnsubscribeFromAppSettings(SettingsUpdateDelegate callback = null)
        {
            lock (_callbacksLock)
            {
                var removedCount = 0;
                TierDataCenterCallback[] callbacks;
                if (_callbacksByApp.TryGetValue(ApplicationName, out callbacks))
                {
                    var callbackList = new List<TierDataCenterCallback>(callbacks);
                    for (var i = callbackList.Count - 1; i >= 0; i--)
                    {
                        var c = callbackList[i];

                        if (callback == null || c.Callback == callback)
                        {
                            callbackList.RemoveAt(i);
                            removedCount++;
                        }
                    }

                    if (removedCount > 0)
                        _callbacksByApp[ApplicationName] = callbackList.ToArray();
                }

                return removedCount;
            }
        }

        /// <summary>
        /// Causes NFig to reload settings for the app, and notifies all registered callbacks. However,
        /// if the settings haven't changed since the last time a callback was called, it will be skipped.
        /// </summary>
        protected void TriggerReload()
        {
            ReloadAndNotifyCallback(GetCallbacks());
        }

        void PollForChanges(object _)
        {
            var commit = GetCurrentCommit();

            var notify = true;
            lock (_dataCacheLock)
            {
                AppSnapshot<TSubApp, TTier, TDataCenter> snapshot;
                if (_dataCache.TryGetValue(ApplicationName, out snapshot))
                {
                    notify = snapshot.Commit != commit;
                }
            }

            if (notify)
            {
                TriggerReload();
            }
        }

        TierDataCenterCallback[] GetCallbacks()
        {
            lock (_callbacksLock)
            {
                TierDataCenterCallback[] callbacks;
                if (_callbacksByApp.TryGetValue(ApplicationName, out callbacks))
                    return callbacks;

                return new TierDataCenterCallback[0];
            }
        }

// === Helpers ===

        protected static string NewCommit()
        {
            return Guid.NewGuid().ToString();
        }

        protected static string GetOverrideKey(string settingName, TDataCenter dataCenter)
        {
            return ":0:" + Convert.ToUInt32(dataCenter) + ";" + settingName;
        }

        static readonly Regex s_keyRegex = new Regex(@"^:\d+:(?<DataCenter>\d+);(?<Name>.+)$");
        protected static bool TryGetValueFromOverride(string key, string stringValue, out SettingValue<TSubApp, TTier, TDataCenter> value)
        {
            var match = s_keyRegex.Match(key);
            if (match.Success)
            {
                value = new SettingValue<TSubApp, TTier, TDataCenter>(
                    match.Groups["Name"].Value,
                    stringValue,
                    (TDataCenter)Enum.ToObject(typeof(TDataCenter), int.Parse(match.Groups["DataCenter"].Value)));

                return true;
            }

            value = null;
            return false;
        }

        protected void AssertValidStringForSetting(string settingName, string value, TDataCenter dataCenter)
        {
            if (!IsValidStringForSetting(settingName, value))
            {
                throw new InvalidSettingValueException(
                    "\"" + value + "\" is not a valid value for setting \"" + settingName + "\"",
                    settingName,
                    value,
                    false,
                    dataCenter.ToString());
            }
        }

        InvalidSettingOverridesException GetSettingsObjectFromData(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot, out TSettings settings)
        {
            return _factory.TryGetAppSettings(out settings, snapshot.Commit, snapshot.Overrides);
        }

        void LogAndNotifyChange(AppSnapshot<TSubApp, TTier, TDataCenter> snapshot)
        {
            try
            {
                PushUpdateNotification();
            }
            finally
            {
                // still want to try logging even if the push notification throws an exception
                Logger?.Log(snapshot);
            }
        }
    }
}