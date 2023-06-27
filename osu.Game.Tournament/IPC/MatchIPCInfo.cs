// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Tournament.Models;

namespace osu.Game.Tournament.IPC
{
    public partial class MatchIPCInfo : Component
    {
        public Bindable<TournamentBeatmap> Beatmap { get; } = new Bindable<TournamentBeatmap>();
        public Bindable<string> Replayer { get; } = new Bindable<string>("");
        public Bindable<string> Slot { get; } = new Bindable<string>("");
        public Bindable<LegacyMods> Mods { get; } = new Bindable<LegacyMods>();
        public Bindable<TourneyState> State { get; } = new Bindable<TourneyState>();
        public Bindable<string> ChatChannel { get; } = new Bindable<string>();
        public BindableInt Score1 { get; } = new BindableInt();
        public BindableInt Score2 { get; } = new BindableInt();
        public BindableBool ShouldUseMult { get; } = new BindableBool();
        public BindableFloat Accuracy1 { get; } = new BindableFloat();
        public BindableFloat Accuracy2 { get; } = new BindableFloat();
        public BindableInt MissCount1 { get; } = new BindableInt();
        public BindableInt MissCount2 { get; } = new BindableInt();
    }
}
