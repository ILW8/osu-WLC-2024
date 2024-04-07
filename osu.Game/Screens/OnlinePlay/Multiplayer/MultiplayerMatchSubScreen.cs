// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Overlays.Dialog;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.OnlinePlay.Components;
using osu.Game.Screens.OnlinePlay.Match;
using osu.Game.Screens.OnlinePlay.Match.Components;
using osu.Game.Screens.OnlinePlay.Multiplayer.Match;
using osu.Game.Screens.OnlinePlay.Multiplayer.Match.Playlist;
using osu.Game.Screens.OnlinePlay.Multiplayer.Participants;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play.HUD;
using osu.Game.Users;
using osuTK;
using ParticipantsList = osu.Game.Screens.OnlinePlay.Multiplayer.Participants.ParticipantsList;

namespace osu.Game.Screens.OnlinePlay.Multiplayer
{
    public class PoolMap
    {
        [JsonProperty("beatmapID")]
        public int BeatmapID;

        [JsonProperty("mods")]
        public Dictionary<string, List<object>> Mods;

        [JsonIgnore]
        public List<Mod> ParsedMods;
    }

    [Serializable]
    public class Pool
    {
        [JsonProperty]
        public readonly BindableList<PoolMap> Beatmaps = new BindableList<PoolMap>();
    }

    public partial class ChatTimerHandler : Component
    {
        protected readonly MultiplayerCountdown MultiplayerChatTimerCountdown = new MatchStartCountdown { TimeRemaining = TimeSpan.Zero };
        protected double CountdownChangeTime;

        private TimeSpan countdownTimeRemaining
        {
            get
            {
                double timeElapsed = Time.Current - CountdownChangeTime;
                TimeSpan remaining;

                if (timeElapsed > MultiplayerChatTimerCountdown.TimeRemaining.TotalMilliseconds)
                    remaining = TimeSpan.Zero;
                else
                    remaining = MultiplayerChatTimerCountdown.TimeRemaining - TimeSpan.FromMilliseconds(timeElapsed);

                return remaining;
            }
        }

        [CanBeNull]
        private ScheduledDelegate countdownUpdateDelegate;

        [Resolved]
        protected MultiplayerClient Client { get; private set; }

        [Resolved]
        protected ChannelManager ChannelManager { get; private set; }

        protected Channel TargetChannel;

        public event Action<string> OnChatMessageDue;

        [BackgroundDependencyLoader]
        private void load()
        {
            Client.RoomUpdated += () =>
            {
                if (Client.Room?.State is MultiplayerRoomState.Open or MultiplayerRoomState.Results)
                    return; // only allow timer if room is idle

                if (countdownUpdateDelegate == null)
                    return;

                Logger.Log($@"Timer scheduled delegate called, room state is {Client.Room?.State}");
                countdownUpdateDelegate?.Cancel();
                countdownUpdateDelegate = null;
                OnChatMessageDue?.Invoke(@"Countdown aborted (game started)");
            };
        }

        public void SetTimer(TimeSpan duration, double startTime, Channel targetChannel)
        {
            // OnChatMessageDue = null;
            MultiplayerChatTimerCountdown.TimeRemaining = duration;
            CountdownChangeTime = startTime;
            TargetChannel = targetChannel;

            countdownUpdateDelegate?.Cancel();
            countdownUpdateDelegate = Scheduler.Add(sendTimerMessage);
        }

        private void processTimerEvent()
        {
            countdownUpdateDelegate?.Cancel();

            double timeToNextMessage = countdownTimeRemaining.TotalSeconds switch
            {
                > 60 => countdownTimeRemaining.TotalMilliseconds % 60_000,
                > 30 => countdownTimeRemaining.TotalMilliseconds % 30_000,
                > 10 => countdownTimeRemaining.TotalMilliseconds % 10_000,
                _ => countdownTimeRemaining.TotalMilliseconds % 5_000
            };

            Logger.Log($@"Time until next timer message: {timeToNextMessage}ms");

            countdownUpdateDelegate = Scheduler.AddDelayed(sendTimerMessage, timeToNextMessage);
        }

        private void sendTimerMessage()
        {
            int secondsRemaining = (int)Math.Round(countdownTimeRemaining.TotalSeconds);
            string message = secondsRemaining == 0 ? @"Countdown finished" : $@"Countdown ends in {secondsRemaining} seconds";
            OnChatMessageDue?.Invoke(message);

            if (secondsRemaining <= 0) return;

            Logger.Log($@"Sent timer message, {secondsRemaining} seconds remaining on timer. ");
            countdownUpdateDelegate = Scheduler.AddDelayed(processTimerEvent, 100);
        }

        public void Abort()
        {
            countdownUpdateDelegate?.Cancel();
            countdownUpdateDelegate = null;
        }
    }

    [Cached]
    public partial class MultiplayerMatchSubScreen : RoomSubScreen, IHandlePresentBeatmap
    {
        public override string Title { get; }

        public override string ShortTitle => "room";
        private LinkFlowContainer linkFlowContainer = null!;

        [Resolved]
        private MultiplayerClient client { get; set; }

        [Resolved]
        private OngoingOperationTracker operationTracker { get; set; } = null!;

        private IDisposable selectionOperation;

        [Resolved(canBeNull: true)]
        private OsuGame game { get; set; }

        [Resolved]
        private BeatmapLookupCache beatmapLookupCache { get; set; } = null!;

        [Resolved]
        private BeatmapModelDownloader beatmapsDownloader { get; set; } = null!;

        // private BeatmapDownloadTracker beatmapDownloadTracker = null!;
        private readonly List<BeatmapDownloadTracker> beatmapDownloadTrackers = new List<BeatmapDownloadTracker>();

        private readonly List<MultiplayerPlaylistItem> playlistItemsToAdd = new List<MultiplayerPlaylistItem>();

        private readonly Queue<APIBeatmapSet> downloadQueue = new Queue<APIBeatmapSet>();

        private AddItemButton addItemButton;

        private OsuTextBox poolInputTextBox;

        public MultiplayerMatchSubScreen(Room room)
            : base(room)
        {
            Title = room.RoomID.Value == null ? "New room" : room.Name.Value;
            Activity.Value = new UserActivity.InLobby(room);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            BeatmapAvailability.BindValueChanged(updateBeatmapAvailability, true);
            UserMods.BindValueChanged(onUserModsChanged);

            client.LoadRequested += onLoadRequested;
            client.RoomUpdated += onRoomUpdated;

            if (!client.IsConnected.Value)
                handleRoomLost();

            Scheduler.Add(processDownloadQueue);
        }

        protected override bool IsConnected => base.IsConnected && client.IsConnected.Value;

        private async Task replacePlaylistItems(IEnumerable<MultiplayerPlaylistItem> items)
        {
            // ensure user is host
            if (!client.IsHost)
                return;

            selectionOperation = operationTracker.BeginOperation();

            var itemsToRemove = Room.Playlist?.ToArray() ?? Array.Empty<PlaylistItem>();

            foreach (var playlistItem in items)
            {
                await client.AddPlaylistItem(playlistItem).ConfigureAwait(true);
            }

            foreach (var playlistItem in itemsToRemove)
            {
                await client.RemovePlaylistItem(playlistItem.ID).ConfigureAwait(false);
            }

            selectionOperation?.Dispose();
        }

        private void processDownloadQueue()
        {
            lock (downloadQueue)
            {
                if (downloadQueue.Count > 0)
                {
                    var beatmapSet = downloadQueue.Dequeue();
                    beatmapsDownloader.Download(beatmapSet);

                    Scheduler.AddDelayed(processDownloadQueue, 2500);
                    return;
                }
            }

            // no message has been posted
            Scheduler.AddDelayed(processDownloadQueue, 50);
        }

        protected override Drawable CreateMainContent() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Horizontal = 5, Vertical = 10 },
            Child = new OsuContextMenuContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ColumnDimensions = new[]
                    {
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 10),
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 10),
                        new Dimension(),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            // Participants column
                            new GridContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                RowDimensions = new[]
                                {
                                    new Dimension(GridSizeMode.AutoSize)
                                },
                                Content = new[]
                                {
                                    new Drawable[] { new ParticipantsListHeader() },
                                    new Drawable[]
                                    {
                                        new ParticipantsList
                                        {
                                            RelativeSizeAxes = Axes.Both
                                        },
                                    }
                                }
                            },
                            // Spacer
                            null,
                            // Beatmap column
                            new GridContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Content = new[]
                                {
                                    new Drawable[] { new OverlinedHeader("Beatmap") },
                                    new Drawable[]
                                    {
                                        addItemButton = new AddItemButton
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Height = 40,
                                            Text = "Add item",
                                            Action = () => OpenSongSelection()
                                        },
                                    },
                                    new Drawable[]
                                    {
                                        new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Direction = FillDirection.Horizontal,
                                            Children = new Drawable[]
                                            {
                                                poolInputTextBox = new OsuTextBox
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    Height = 40,
                                                    Width = 0.6f,
                                                    LengthLimit = 262144
                                                },
                                                new PurpleRoundedButton
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    Height = 40,
                                                    Width = 0.4f,
                                                    Text = @"Load pool",
                                                    Action = loadPoolFromJson
                                                }
                                            }
                                        }
                                    },
                                    new Drawable[]
                                    {
                                        new MultiplayerPlaylist
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            RequestEdit = OpenSongSelection
                                        }
                                    },
                                    new[]
                                    {
                                        UserModsSection = new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Margin = new MarginPadding { Top = 10 },
                                            Alpha = 0,
                                            Children = new Drawable[]
                                            {
                                                new OverlinedHeader("Extra mods"),
                                                new FillFlowContainer
                                                {
                                                    AutoSizeAxes = Axes.Both,
                                                    Direction = FillDirection.Horizontal,
                                                    Spacing = new Vector2(10, 0),
                                                    Children = new Drawable[]
                                                    {
                                                        new UserModSelectButton
                                                        {
                                                            Anchor = Anchor.CentreLeft,
                                                            Origin = Anchor.CentreLeft,
                                                            Width = 90,
                                                            Text = "Select",
                                                            Action = ShowUserModSelect,
                                                        },
                                                        new ModDisplay
                                                        {
                                                            Anchor = Anchor.CentreLeft,
                                                            Origin = Anchor.CentreLeft,
                                                            Current = UserMods,
                                                            Scale = new Vector2(0.8f),
                                                        },
                                                    }
                                                },
                                            }
                                        },
                                    },
                                },
                                RowDimensions = new[]
                                {
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(),
                                    new Dimension(GridSizeMode.AutoSize),
                                }
                            },
                            // Spacer
                            null,
                            // Main right column
                            new GridContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Content = new[]
                                {
                                    new Drawable[] { new OverlinedHeader("Lobby ID") },
                                    new Drawable[] { linkFlowContainer = new LinkFlowContainer { Height = 24 } },
                                    new Drawable[] { new OverlinedHeader("Chat") },
                                    new Drawable[] { chatDisplay = new MatchChatDisplay(Room) { RelativeSizeAxes = Axes.Both } }
                                },
                                RowDimensions = new[]
                                {
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(),
                                }
                            },
                        }
                    }
                }
            }
        };

        private void loadPoolFromJson()
        {
            Pool pool = JsonConvert.DeserializeObject<Pool>(
                poolInputTextBox.Current.Value,
                new JsonSerializerSettings
                {
                    Error = delegate(object _, ErrorEventArgs args) { args.ErrorContext.Handled = true; }
                }
            );

            if (pool.Beatmaps == null)
                return;

            foreach (var map in pool.Beatmaps)
            {
                var mods = map.Mods;

                List<Mod> modInstances = new List<Mod>();

                foreach ((string modKey, var modParams) in mods)
                {
                    var osuRuleset = Rulesets.GetRuleset(0)?.CreateInstance();
                    if (osuRuleset == null) continue;

                    Mod modInstance = StandAloneChatDisplay.ParseMod(osuRuleset, modKey, modParams);
                    if (modInstance != null)
                        modInstances.Add(modInstance);
                }

                map.ParsedMods = new List<Mod>();
                map.ParsedMods.AddRange(modInstances);
            }

            // download map and set playlist
            beatmapLookupCache.GetBeatmapsAsync(pool.Beatmaps.Select(map => map.BeatmapID).ToArray()).ContinueWith(task => Schedule(() =>
            {
                APIBeatmap[] beatmaps = task.GetResultSafely();

                playlistItemsToAdd.Clear();

                var playlistItems = beatmaps
                                    .Where(beatmap => beatmap != null && pool.Beatmaps.Select(b => b.BeatmapID).Contains(beatmap.OnlineID))
                                    .Select(beatmap =>
                                    {
                                        var mods = pool.Beatmaps
                                                       .FirstOrDefault(poolMap => poolMap.BeatmapID == beatmap.OnlineID)?
                                                       .ParsedMods
                                                       .Select(mod => new APIMod(mod))
                                                       .ToArray();

                                        var item = new PlaylistItem(beatmap)
                                        {
                                            RulesetID = beatmap.Ruleset.OnlineID,
                                            RequiredMods = mods ?? Array.Empty<APIMod>(),
                                            AllowedMods = Array.Empty<APIMod>()
                                        };

                                        return new MultiplayerPlaylistItem
                                        {
                                            ID = 0,
                                            BeatmapID = item.Beatmap.OnlineID,
                                            BeatmapChecksum = item.Beatmap.MD5Hash,
                                            RulesetID = item.RulesetID,
                                            RequiredMods = item.RequiredMods,
                                            AllowedMods = item.AllowedMods
                                        };
                                    });

                var multiplayerPlaylistItems = playlistItems.ToArray();

                if (multiplayerPlaylistItems.Length != pool.Beatmaps.Count)
                {
                    Logger.Log($@"Expected {pool.Beatmaps.Count} maps, beatmap lookup returned {multiplayerPlaylistItems.Length} maps, aborting!",
                        LoggingTarget.Runtime, LogLevel.Important);
                    return;
                }

                foreach (var beatmap in beatmaps)
                {
                    if (beatmap?.BeatmapSet == null) continue;

                    BeatmapDownloadTracker tracker = new BeatmapDownloadTracker(beatmap.BeatmapSet);
                    AddInternal(tracker); // a leak, but I can't be bothered figuring out why BeatmapDownloadTracker doesn't work inside another container
                    beatmapDownloadTrackers.Add(tracker);

                    tracker.State.BindValueChanged(changeEvent =>
                    {
                        // download failed, abort.
                        if (changeEvent.OldValue == DownloadState.Downloading && changeEvent.NewValue == DownloadState.NotDownloaded)
                        {
                            tracker.State.UnbindAll();
                            beatmapDownloadTrackers.Remove(tracker);
                            RemoveInternal(tracker, true);
                            return;
                        }

                        switch (changeEvent.NewValue)
                        {
                            case DownloadState.LocallyAvailable:
                                tracker.State.UnbindAll();
                                beatmapDownloadTrackers.Remove(tracker);
                                RemoveInternal(tracker, true);

                                // what the fuck is this shit...
                                playlistItemsToAdd.Add(multiplayerPlaylistItems.FirstOrDefault(item => item.BeatmapID == beatmap.OnlineID));

                                if (playlistItemsToAdd.Count == multiplayerPlaylistItems.Length) // all maps in playlist are downloaded and ready
                                {
                                    // ReSharper disable once AsyncVoidLambda
                                    Scheduler.Add(async () => await replacePlaylistItems(
                                                                      playlistItemsToAdd
                                                                          .OrderBy(mpPlaylistItem => pool.Beatmaps.Select(map => map.BeatmapID).ToList().IndexOf(mpPlaylistItem.BeatmapID))
                                                                          .ToArray())
                                                                  .ConfigureAwait(false));
                                }

                                return;

                            case DownloadState.NotDownloaded:
                                Logger.Log($@"Downloading beatmapset {beatmap.BeatmapSet.OnlineID}");
                                lock (downloadQueue)
                                    downloadQueue.Enqueue(beatmap.BeatmapSet);
                                break;

                            case DownloadState.Unknown:
                                break;

                            case DownloadState.Downloading:
                                break;

                            case DownloadState.Importing:
                                break;

                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }, true);
                }
            }));
        }

        /// <summary>
        /// Opens the song selection screen to add or edit an item.
        /// </summary>
        /// <param name="itemToEdit">An optional playlist item to edit. If null, a new item will be added instead.</param>
        internal void OpenSongSelection(PlaylistItem itemToEdit = null)
        {
            if (!this.IsCurrentScreen())
                return;

            this.Push(new MultiplayerMatchSongSelect(Room, itemToEdit));
        }

        protected override Drawable CreateFooter() => new MultiplayerMatchFooter();

        protected override RoomSettingsOverlay CreateRoomSettingsOverlay(Room room) => new MultiplayerMatchSettingsOverlay(room);

        protected override void UpdateMods()
        {
            if (SelectedItem.Value == null || client.LocalUser == null || !this.IsCurrentScreen())
                return;

            // update local mods based on room's reported status for the local user (omitting the base call implementation).
            // this makes the server authoritative, and avoids the local user potentially setting mods that the server is not aware of (ie. if the match was started during the selection being changed).
            var rulesetInstance = Rulesets.GetRuleset(SelectedItem.Value.RulesetID)?.CreateInstance();
            Debug.Assert(rulesetInstance != null);
            Mods.Value = client.LocalUser.Mods.Select(m => m.ToMod(rulesetInstance)).Concat(SelectedItem.Value.RequiredMods.Select(m => m.ToMod(rulesetInstance))).ToList();
        }

        [Resolved(canBeNull: true)]
        private IDialogOverlay dialogOverlay { get; set; }

        [Resolved]
        private ChatTimerHandler chatTimerHandler { get; set; }

        private bool exitConfirmed;

        public override void OnResuming(ScreenTransitionEvent e)
        {
            // chatTimerHandler.SetMessageHandler(chatDisplay.EnqueueMessageBot);
            chatTimerHandler.OnChatMessageDue += chatDisplay.EnqueueBotMessage;
            base.OnResuming(e);
        }

        public override void OnSuspending(ScreenTransitionEvent _)
        {
            chatTimerHandler.OnChatMessageDue -= chatDisplay.EnqueueBotMessage;
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            // chatTimerHandler.SetMessageHandler(chatDisplay.EnqueueMessageBot);
            chatTimerHandler.OnChatMessageDue += chatDisplay.EnqueueBotMessage;
            base.OnEntering(e);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            chatTimerHandler.OnChatMessageDue -= chatDisplay.EnqueueBotMessage;

            // room has not been created yet or we're offline; exit immediately.
            if (client.Room == null || !IsConnected)
                return base.OnExiting(e);

            if (!exitConfirmed && dialogOverlay != null)
            {
                if (dialogOverlay.CurrentDialog is ConfirmDialog confirmDialog)
                    confirmDialog.PerformOkAction();
                else
                {
                    dialogOverlay.Push(new ConfirmDialog("Are you sure you want to leave this multiplayer match?", () =>
                    {
                        exitConfirmed = true;
                        if (this.IsCurrentScreen())
                            this.Exit();
                    }));
                }

                return true;
            }

            return base.OnExiting(e);
        }

        private ModSettingChangeTracker modSettingChangeTracker;
        private ScheduledDelegate debouncedModSettingsUpdate;
        private StandAloneChatDisplay chatDisplay;

        private void onUserModsChanged(ValueChangedEvent<IReadOnlyList<Mod>> mods)
        {
            modSettingChangeTracker?.Dispose();

            if (client.Room == null)
                return;

            client.ChangeUserMods(mods.NewValue).FireAndForget();

            modSettingChangeTracker = new ModSettingChangeTracker(mods.NewValue);
            modSettingChangeTracker.SettingChanged += onModSettingsChanged;
        }

        private void onModSettingsChanged(Mod mod)
        {
            // Debounce changes to mod settings so as to not thrash the network.
            debouncedModSettingsUpdate?.Cancel();
            debouncedModSettingsUpdate = Scheduler.AddDelayed(() =>
            {
                if (client.Room == null)
                    return;

                client.ChangeUserMods(UserMods.Value).FireAndForget();
            }, 500);
        }

        private void updateBeatmapAvailability(ValueChangedEvent<BeatmapAvailability> availability)
        {
            if (client.Room == null)
                return;

            client.ChangeBeatmapAvailability(availability.NewValue).FireAndForget();

            switch (availability.NewValue.State)
            {
                case DownloadState.LocallyAvailable:
                    if (client.LocalUser?.State == MultiplayerUserState.Spectating
                        && (client.Room?.State == MultiplayerRoomState.WaitingForLoad || client.Room?.State == MultiplayerRoomState.Playing))
                    {
                        onLoadRequested();
                    }

                    break;

                case DownloadState.Unknown:
                    // Don't do anything rash in an unknown state.
                    break;

                default:
                    // while this flow is handled server-side, this covers the edge case of the local user being in a ready state and then deleting the current beatmap.
                    if (client.LocalUser?.State == MultiplayerUserState.Ready)
                        client.ChangeState(MultiplayerUserState.Idle);
                    break;
            }
        }

        private void onRoomUpdated()
        {
            // may happen if the client is kicked or otherwise removed from the room.
            if (client.Room == null)
            {
                handleRoomLost();
                return;
            }

            updateCurrentItem();

            addItemButton.Alpha = localUserCanAddItem ? 1 : 0;

            Scheduler.AddOnce(UpdateMods);
            Scheduler.AddOnce(() =>
            {
                string roomLink = $"https://{MessageFormatter.WebsiteRootUrl}/multiplayer/rooms/{Room.RoomID}";
                linkFlowContainer.Clear();
                linkFlowContainer.AddLink(roomLink, roomLink);
            });

            Activity.Value = new UserActivity.InLobby(Room);
        }

        private bool localUserCanAddItem => client.IsHost || Room.QueueMode.Value != QueueMode.HostOnly;

        private void updateCurrentItem()
        {
            Debug.Assert(client.Room != null);
            SelectedItem.Value = Room.Playlist.SingleOrDefault(i => i.ID == client.Room.Settings.PlaylistItemId);
        }

        private void handleRoomLost() => Schedule(() =>
        {
            Logger.Log($"{this} exiting due to loss of room or connection");

            if (this.IsCurrentScreen())
                this.Exit();
            else
                ValidForResume = false;
        });

        private void onLoadRequested()
        {
            // In the case of spectating, IMultiplayerClient.LoadRequested can be fired while the game is still spectating a previous session.
            // For now, we want to game to switch to the new game so need to request exiting from the play screen.
            if (!ParentScreen.IsCurrentScreen())
            {
                ParentScreen.MakeCurrent();

                Schedule(onLoadRequested);
                return;
            }

            // The beatmap is queried asynchronously when the selected item changes.
            // This is an issue with MultiSpectatorScreen which is effectively in an always "ready" state and receives LoadRequested() callbacks
            // even when it is not truly ready (i.e. the beatmap hasn't been selected by the client yet). For the time being, a simple fix to this is to ignore the callback.
            // Note that spectator will be entered automatically when the client is capable of doing so via beatmap availability callbacks (see: updateBeatmapAvailability()).
            if (client.LocalUser?.State == MultiplayerUserState.Spectating && (SelectedItem.Value == null || Beatmap.IsDefault))
                return;

            if (BeatmapAvailability.Value.State != DownloadState.LocallyAvailable)
                return;

            StartPlay();
        }

        protected override Screen CreateGameplayScreen()
        {
            Debug.Assert(client.LocalUser != null);
            Debug.Assert(client.Room != null);

            // force using room Users order when collecting players
            // int[] userIds = client.CurrentMatchPlayingUserIds.ToArray();
            int[] userIds = client.Room.Users.Where(u => u.State >= MultiplayerUserState.WaitingForLoad && u.State <= MultiplayerUserState.FinishedPlay).Select(u => u.UserID).ToArray();
            MultiplayerRoomUser[] users = userIds.Select(id => client.Room.Users.First(u => u.UserID == id)).ToArray();

            switch (client.LocalUser.State)
            {
                case MultiplayerUserState.Spectating:
                    return new MultiSpectatorScreen(Room, users.Take(PlayerGrid.MAX_PLAYERS).ToArray());

                default:
                    return new MultiplayerPlayerLoader(() => new MultiplayerPlayer(Room, SelectedItem.Value, users));
            }
        }

        public void PresentBeatmap(WorkingBeatmap beatmap, RulesetInfo ruleset)
        {
            if (!this.IsCurrentScreen())
                return;

            if (!localUserCanAddItem)
                return;

            // If there's only one playlist item and we are the host, assume we want to change it. Else add a new one.
            PlaylistItem itemToEdit = client.IsHost && Room.Playlist.Count == 1 ? Room.Playlist.Single() : null;

            OpenSongSelection(itemToEdit);

            // Re-run PresentBeatmap now that we've pushed a song select that can handle it.
            game?.PresentBeatmap(beatmap.BeatmapSetInfo, b => b.ID == beatmap.BeatmapInfo.ID);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (client != null)
            {
                client.RoomUpdated -= onRoomUpdated;
                client.LoadRequested -= onLoadRequested;
            }

            modSettingChangeTracker?.Dispose();
        }

        public partial class AddItemButton : PurpleRoundedButton
        {
        }
    }
}
