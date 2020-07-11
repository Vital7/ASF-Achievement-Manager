using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Localization;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Internal;

namespace ASFAchievementManager {
	public sealed class AchievementHandler : ClientMsgHandler {
		private readonly Bot Bot;
		private readonly ConcurrentDictionary<EMsg, TaskCompletionSource<ClientMsgProtobuf>> MessagesToWait = new ConcurrentDictionary<EMsg, TaskCompletionSource<ClientMsgProtobuf>>();

		internal AchievementHandler(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public override void HandleMsg(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				Bot.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			switch (packetMsg.MsgType) {
				case EMsg.ClientGetUserStatsResponse:
					HandleGetUserStatsResponse(packetMsg);
					break;
				case EMsg.ClientStoreUserStatsResponse:
					HandleStoreUserStatsResponse(packetMsg);
					break;
			}
		}

		private async Task<T> GetResponse<T>(IClientMsg request, EMsg expectedResponseType) where T : ClientMsgProtobuf {
			if ((request == null) || (expectedResponseType == EMsg.Invalid)) {
				Bot.ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			if (Client == null) {
				Bot.ArchiLogger.LogNullError(nameof(Client));
				return null;
			}

			MessagesToWait[expectedResponseType] = new TaskCompletionSource<ClientMsgProtobuf>();
			Client.Send(request);

			Task<ClientMsgProtobuf> task = MessagesToWait[expectedResponseType].Task;
			CancellationTokenSource cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(ASF.GlobalConfig.ConnectionTimeout));
			if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(ASF.GlobalConfig.ConnectionTimeout), cancellationToken.Token)).ConfigureAwait(false) == task) {
				cancellationToken.Cancel();
				return (T) await task.ConfigureAwait(false);
			}

			MessagesToWait[expectedResponseType].SetCanceled();
			Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorFailingRequest, expectedResponseType.ToString()));
			return null;

		}

		private void HandleGetUserStatsResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				Bot.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> response = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);

			if (MessagesToWait.ContainsKey(packetMsg.MsgType)) {
				MessagesToWait[packetMsg.MsgType].TrySetResult(response);
			}
		}

		private void HandleStoreUserStatsResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				Bot.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> response = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packetMsg);
			if (response.Body.game_id == 0) {
				Bot.ArchiLogger.LogNullError(nameof(response.Body.game_id));
			}

			if (MessagesToWait.ContainsKey(packetMsg.MsgType)) {
				MessagesToWait[packetMsg.MsgType].TrySetResult(response);
			}
		}

		#region Utilities

		private static List<StatData> ParseResponse(CMsgClientGetUserStatsResponse response) {
			List<StatData> result = new List<StatData>();
			KeyValue keyValues = new KeyValue();
			if (response.schema != null) {
				using (MemoryStream ms = new MemoryStream(response.schema)) {
					if (!keyValues.TryReadAsBinary(ms)) {
						ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(response.schema)));
						return null;
					}
				}

				// At first we enumerate all real achievements
				foreach (KeyValue stat in keyValues["stats"].Children) {
					
					if (stat["type"].Value == "4") {
						foreach (KeyValue achievement in stat["bits"].Children) {
							int bitNum = int.Parse(achievement.Name!);
							uint statNum = uint.Parse(stat.Name!);
							bool isSet = false;

							CMsgClientGetUserStatsResponse.Stats statObject = response.stats?.Find(statElement => statElement.stat_id == statNum);
							if (statObject != null) {
								isSet = (statObject.stat_value & ((uint) 1 << bitNum)) != 0;
							}

							bool restricted = achievement["permission"] != KeyValue.Invalid;

							bool isDependent = achievement["progress"] == KeyValue.Invalid;
							string dependencyName = isDependent ? "" : achievement["progress"]["value"]["operand1"].Value;

							uint dependencyValue = isDependent ? 0 : uint.Parse(achievement["progress"]["max_val"].Value!);
							string lang = CultureInfo.CurrentUICulture.EnglishName.ToLower();
							if (lang.IndexOf('(') > 0) {
								lang = lang.Substring(0, lang.IndexOf('(') - 1);
							}

							if (achievement["display"]["name"][lang] == KeyValue.Invalid) {
								lang = "english"; //fallback to english
							}

							string name = achievement["display"]["name"][lang].Value;

							result.Add(new StatData {
								StatNum = statNum,
								BitNum = bitNum,
								IsSet = isSet,
								Restricted = restricted,
								DependencyValue = dependencyValue,
								DependencyName = dependencyName,
								Dependency = 0,
								Name = name
							});
						}
					}
				}

				// Now we update all dependencies
				foreach (KeyValue stat in keyValues["stats"].Children) {
					if (stat["type"].AsUnsignedByte() == 1) {
						uint statNum = uint.Parse(stat.Name!);
						bool restricted = stat["permission"] != KeyValue.Invalid;
						string name = stat["name"].Value;
						StatData parentStat = result.Find(item => item.DependencyName == name);
						if (parentStat != null) {
							parentStat.Dependency = statNum;
							if (restricted && !parentStat.Restricted) {
								parentStat.Restricted = true;
							}
						}
					}
				}
			}

			return result;
		}

		private static void SetStat(List<CMsgClientStoreUserStats2.Stats> statsToSet, IReadOnlyList<StatData> stats, CMsgClientGetUserStatsResponse storedResponse, int achievementNum, bool set = true) {
			if ((achievementNum < 0) || (achievementNum > stats.Count)) {
				return; //it should never happen
			}

			CMsgClientStoreUserStats2.Stats currentstat = statsToSet.Find(stat => stat.stat_id == stats[achievementNum].StatNum);
			if (currentstat == null) {
				CMsgClientGetUserStatsResponse.Stats statValue = storedResponse.stats.Find(stat => stat.stat_id == stats[achievementNum].StatNum);
				currentstat = new CMsgClientStoreUserStats2.Stats {
					stat_id = stats[achievementNum].StatNum,
					stat_value = statValue?.stat_value ?? 0
				};

				statsToSet.Add(currentstat);
			}

			if (set) {
				currentstat.stat_value |= (uint) 1 << stats[achievementNum].BitNum;
			} else {
				currentstat.stat_value &= ~((uint) 1 << stats[achievementNum].BitNum);
			}

			if (stats[achievementNum].DependencyName != "") {
				CMsgClientStoreUserStats2.Stats dependencyStat = statsToSet.Find(stat => stat.stat_id == stats[achievementNum].Dependency);
				if (dependencyStat == null) {
					dependencyStat = new CMsgClientStoreUserStats2.Stats {
						stat_id = stats[achievementNum].Dependency,
						stat_value = set ? stats[achievementNum].DependencyValue : 0
					};

					statsToSet.Add(dependencyStat);
				}
			}
		}

		#endregion

		#region Endpoints

		[PublicAPI]
		public async Task<(List<StatData> Stats, string Response)> GetAchievements(uint appID) {
			if (appID == 0) {
				Bot.ArchiLogger.LogNullError(nameof(appID));
				return default;
			}

			if (Client == null) {
				Bot.ArchiLogger.LogNullError(nameof(Client));
				return default;
			}

			if (!Client.IsConnected) {
				return (null, Strings.BotNotConnected);
			}

			ClientMsgProtobuf<CMsgClientGetUserStats> request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats) {
				Body = {
					game_id = appID,
					steam_id_for_user = Bot.SteamID
				}
			};

			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> userStatsResponse = await GetResponse<ClientMsgProtobuf<CMsgClientGetUserStatsResponse>>(request, EMsg.ClientGetUserStatsResponse).ConfigureAwait(false);
			if (userStatsResponse == null) {
				return (null, $"Can't retrieve achievements for {appID}");
			}

			List<string> responses = new List<string>();
			List<StatData> stats = ParseResponse(userStatsResponse.Body);

			const char checkMarkEmoji = '\u2705';
			const char crossMarkEmoji = '\u274C';
			const string warningEmoji = "\u26A0\uFE0F";

			if ((stats == null) || (stats.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(stats));
			} else {
				responses = stats.Select((stat, index) => $"{index + 1,-5}{(stat.IsSet ? checkMarkEmoji : crossMarkEmoji)} {(stat.Restricted ? $"{warningEmoji} " : "")}{stat.Name}").ToList();
			}

			return (stats, responses.Count > 0 ? $"\u200B\nAchievements for {appID}:\n{string.Join(Environment.NewLine, responses)}" : $"Can't retrieve achievements for {appID}");
		}

		[PublicAPI]
		public async Task<(bool Success, string Response)> SetAchievements(uint appID, HashSet<uint> achievements, bool set = true) {
			if ((appID == 0) || (achievements == null)) {
				Bot.ArchiLogger.LogNullError($"{nameof(appID)} || {nameof(achievements)}");
				return (false, null);
			}

			if (Client == null) {
				Bot.ArchiLogger.LogNullError(nameof(Client));
				return default;
			}

			if (!Client.IsConnected) {
				return (false, Strings.BotNotConnected);
			}

			List<string> responses = new List<string>();
			(List<StatData> stats, string response) = await GetAchievements(appID).ConfigureAwait(false);

			ClientMsgProtobuf<CMsgClientGetUserStats> getRequest = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats) {
				Body = {
					game_id = appID,
					steam_id_for_user = Bot.SteamID
				}
			};

			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> message = await GetResponse<ClientMsgProtobuf<CMsgClientGetUserStatsResponse>>(getRequest, EMsg.ClientGetUserStatsResponse).ConfigureAwait(false);
			if ((message == null) || (message.Body.eresult != (int) EResult.OK)) {
				return (false, response);
			}

			List<CMsgClientStoreUserStats2.Stats> statsToSet = new List<CMsgClientStoreUserStats2.Stats>();

			if (achievements.Count == 0) {
				//if no parameters provided - set/reset all. Don't kill me Archi.
				for (int counter = 0; counter < stats.Count; counter++) {
					if (!stats[counter].Restricted) {
						SetStat(statsToSet, stats, message.Body, counter, set);
					}
				}
			} else {
				foreach (uint achievement in achievements) {
					if (stats.Count < achievement) {
						responses.Add($"Achievement #{achievement} is out of range");
						continue;
					}

					if (stats[(int) achievement - 1].IsSet == set) {
						responses.Add($"Achievement #{achievement} is already {(set ? "unlocked" : "locked")}");
						continue;
					}

					if (stats[(int) achievement - 1].Restricted) {
						responses.Add($"Achievement #{achievement} is protected and can't be switched");
						continue;
					}

					SetStat(statsToSet, stats, message.Body, (int) achievement - 1, set);
				}
			}

			if (statsToSet.Count == 0) {
				responses.Add(Strings.WarningFailed);
				return (false, $"\u200B\n{string.Join(Environment.NewLine, responses)}");
			}

			if (responses.Count > 0) {
				responses.Add("Trying to switch remaining achievements..."); //if some errors occured
			}

			ClientMsgProtobuf<CMsgClientStoreUserStats2> request = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2) {
				Body = {
					game_id = appID,
					settor_steam_id = Bot.SteamID,
					settee_steam_id = Bot.SteamID,
					explicit_reset = false,
					crc_stats = message.Body.crc_stats
				}
			};

			request.Body.stats.AddRange(statsToSet);

			ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> storeResponse = await GetResponse<ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>>(request, EMsg.ClientStoreUserStatsResponse).ConfigureAwait(false);

			bool success = (storeResponse != null) && (storeResponse.Body.eresult == 1);
			responses.Add(success ? Strings.Success : Strings.WarningFailed);

			return (success, $"\u200B\n{string.Join(Environment.NewLine, responses)}");
		}

		#endregion
	}
}
