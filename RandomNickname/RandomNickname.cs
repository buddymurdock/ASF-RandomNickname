using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomNickname;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary nickname/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomNickname : IASF, IBotConnection, IGitHubPluginUpdates {
	private const ushort DefaultMaxDelayInDays = 60;
	private const ushort DefaultMinDelayInDays = 14;
	private const byte MaxNicknameLength = 32; // Steam's persona name length limit
	private const string BundledNicknamesFileName = "nicknames.json";

	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);

	// Last nickname successfully set per bot, purely so we don't roll the same one twice in a row
	private readonly ConcurrentDictionary<string, string> BotLastNickname = new(StringComparer.Ordinal);

	private bool Enabled;
	private volatile bool EmptyPoolWarningLogged;
	private ushort MaxDelayInDays = DefaultMaxDelayInDays;
	private ushort MinDelayInDays = DefaultMinDelayInDays;
	private string[] NicknamePool = [];
	private bool UseBundledNicknames;

	public string Name => nameof(RandomNickname);
	public string RepositoryName => "buddymurdock/ASF-RandomNickname";
	public Version Version => typeof(RandomNickname).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomNicknameEnabled / RandomNicknameMinDelayDays / RandomNicknameMaxDelayDays /
	// RandomNicknameNicknames / RandomNicknameUseBundledNicknames from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		HashSet<string> parsedNicknames = [];

		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomNickname)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomNickname)}MinDelayDays" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInDays = minDelay;

						break;
					case $"{nameof(RandomNickname)}MaxDelayDays" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInDays = maxDelay;

						break;
					case $"{nameof(RandomNickname)}UseBundledNicknames" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						UseBundledNicknames = configValue.GetBoolean();

						break;
					case $"{nameof(RandomNickname)}Nicknames" when configValue.ValueKind == JsonValueKind.Array:
						AddParsedNicknames(configValue, parsedNicknames);

						break;
				}
			}
		}

		if (UseBundledNicknames) {
			LoadBundledNicknames(parsedNicknames);
		}

		NicknamePool = [.. parsedNicknames];

		if (MinDelayInDays > MaxDelayInDays) {
			(MinDelayInDays, MaxDelayInDays) = (MaxDelayInDays, MinDelayInDays);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomNickname)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInDays}-{MaxDelayInDays} days each bot randomly changes its nickname, picking from a pool of {NicknamePool.Length} candidate(s).");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotNicknameLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	private async Task BotNicknameLoopAsync(Bot bot, CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			int delayDays = MinDelayInDays == MaxDelayInDays ? MinDelayInDays : Random.Shared.Next(MinDelayInDays, MaxDelayInDays + 1);

			try {
				await LongDelayAsync(TimeSpan.FromDays(delayDays), cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				TrySetRandomNickname(bot);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a single
	// Task.Delay(TimeSpan.FromDays(60)) throws ArgumentOutOfRangeException immediately, which
	// went unhandled here and crashed the entire ASF process via OnUnobservedTaskException.
	// Chunking sidesteps the limit for arbitrarily long delays.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private void TrySetRandomNickname(Bot bot) {
		if (NicknamePool.Length == 0) {
			if (!EmptyPoolWarningLogged) {
				EmptyPoolWarningLogged = true;

				ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomNickname)}Nicknames is empty, set it or {nameof(RandomNickname)}UseBundledNicknames in ASF.json for this plugin to do anything.");
			}

			return;
		}

		BotLastNickname.TryGetValue(bot.BotName, out string? lastNickname);

		string[] candidates = (NicknamePool.Length > 1) && (lastNickname != null) ? [.. NicknamePool.Where(nickname => nickname != lastNickname)] : NicknamePool;

		string nickname = candidates[Random.Shared.Next(candidates.Length)];

		// SteamKit2's SetPersonaName() re-sends the last cached persona state alongside the new
		// name (it never overwrites it), so this doesn't fight with plugins like RandomOnlineStatus
		// that manage online status/game state independently - no extra state reset is needed here.
		bot.SteamFriends.SetPersonaName(nickname);

		BotLastNickname[bot.BotName] = nickname;

		bot.ArchiLogger.LogGenericInfo($"Randomly changed nickname to \"{nickname}\".");
	}

	private static void AddParsedNicknames(JsonElement array, HashSet<string> target) {
		foreach (JsonElement nicknameElement in array.EnumerateArray()) {
			string? nickname = nicknameElement.ValueKind == JsonValueKind.String ? nicknameElement.GetString() : null;

			if (!string.IsNullOrWhiteSpace(nickname) && (nickname.Length <= MaxNicknameLength)) {
				target.Add(nickname);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomNickname)}Nicknames entry: {nicknameElement}.");
			}
		}
	}

	// Loads nicknames.json shipped alongside the plugin DLL, adding its entries on top of whatever came from ASF.json
	private static void LoadBundledNicknames(HashSet<string> target) {
		string? pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		if (string.IsNullOrEmpty(pluginDirectory)) {
			ASF.ArchiLogger.LogGenericWarning($"Could not determine plugin directory, {nameof(RandomNickname)}UseBundledNicknames will have no effect.");

			return;
		}

		string filePath = Path.Combine(pluginDirectory, BundledNicknamesFileName);

		if (!File.Exists(filePath)) {
			ASF.ArchiLogger.LogGenericWarning($"{BundledNicknamesFileName} not found next to the plugin, {nameof(RandomNickname)}UseBundledNicknames will have no effect.");

			return;
		}

		List<string>? entries;

		try {
			entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath));
		} catch (JsonException e) {
			ASF.ArchiLogger.LogGenericException(e);

			return;
		}

		if (entries == null) {
			return;
		}

		foreach (string entry in entries) {
			if (!string.IsNullOrWhiteSpace(entry) && (entry.Length <= MaxNicknameLength)) {
				target.Add(entry);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid entry in {BundledNicknamesFileName}: {entry}.");
			}
		}
	}
}
#pragma warning restore CA5394
#pragma warning restore CA1001
#pragma warning restore CA1812
