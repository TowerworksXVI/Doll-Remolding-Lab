using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Project;

public sealed partial class AuthoredEditSession
{
    /// <summary>Commit many authored mutations as ONE transaction. The commands the session already
    /// exposes one at a time — mint a part's slots, create an edit, publish returned bytes onto a slot,
    /// answer a binding, hide a part — are handed to <paramref name="work"/> on a candidate project, and
    /// the whole shape is validated and committed once, under one revision, with one
    /// <see cref="Changed"/> notification naming everything it moved.
    ///
    /// <para>This is what a Blender return is: one modder action that lands a hundred and more answers at
    /// once. Committing them one at a time made the return a hundred separate changes for the pages, the
    /// autosave and the build plan to each answer in turn. It also made a failure part-way through a
    /// half-applied return; here a refusal anywhere commits nothing at all, and the files the batch had
    /// already put in place come back out with it.</para>
    ///
    /// <para>Everything <paramref name="work"/> touches is the candidate, so nothing it reads back is
    /// live until the whole batch commits — which is exactly what lets a publish address an edit the same
    /// batch minted a moment earlier.</para></summary>
    public void Compound(Action<CompoundChange> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ChangeWithFiles((project, files) => work(new CompoundChange(project, files)));
    }

    /// <summary>One compound transaction's working face: the same authored commands the session exposes,
    /// applied to the candidate the batch will commit. It lives only for the duration of the
    /// <see cref="Compound"/> call — holding one afterwards addresses a project that is no longer going
    /// anywhere.</summary>
    public sealed class CompoundChange
    {
        private readonly AuthoredProject _project;
        private readonly TransactionFiles _files;

        internal CompoundChange(AuthoredProject project, TransactionFiles files)
        {
            _project = project;
            _files = files;
        }

        /// <summary>Where the project lives, which every file this batch writes is resolved under.</summary>
        public string RootDir => _project.RootDir
            ?? throw new InvalidOperationException("project has no root directory");

        // ---- reads, against the candidate as this batch has left it so far --------------------------

        /// <summary>Every explicit slot answer in one edit, as the batch has it now.</summary>
        public IReadOnlyList<EditSlotState> Slots(string editDefinitionId) =>
            AuthoredEditSession.Slots(_project, editDefinitionId);

        /// <summary>The part one edit answers for.</summary>
        public TargetPart EditTarget(string editDefinitionId) =>
            Clone(RequiredEdit(_project, editDefinitionId).Target);

        /// <summary>Whether the project holds any place at all for this part — the test a caller makes
        /// before asking the install for one.</summary>
        public bool HasPartSlots(TargetPart target)
        {
            ArgumentNullException.ThrowIfNull(target);
            return _project.TargetSlots.Any(slot => slot.Part.SameAs(target));
        }

        /// <summary>Whether this part's hide edit is already used in Always.</summary>
        public bool HasPlacedHide(TargetPart target)
        {
            ArgumentNullException.ThrowIfNull(target);
            return _project.EditDefinitions.Any(edit => edit.Kind == EditDefinitionKind.Hide
                && edit.Target.SameAs(target)
                && _project.Always.Contains(edit.Id, StringComparer.Ordinal));
        }

        /// <summary>Whether one edit is used in Always.</summary>
        public bool IsPlacedAlways(string editDefinitionId) =>
            _project.Always.Contains(editDefinitionId, StringComparer.Ordinal);

        // ---- mutations ------------------------------------------------------------------------------

        /// <summary>Replace the workbench inventory as part of this transaction.</summary>
        public void SetWorkspaceIndex(AuthoredWorkspaceIndex index)
        {
            ArgumentNullException.ThrowIfNull(index);
            string json = AuthoredProjectSerializer.SerializeWorkspaceIndex(index);
            AuthoredEditSession.SetWorkspaceIndex(_project,
                AuthoredProjectSerializer.DeserializeWorkspaceIndex(json));
        }

        /// <summary>Remove one subject and all intent owned by it as part of this transaction.</summary>
        public void ForgetSubject(string subject, string outfit) =>
            AuthoredEditSession.ForgetSubject(_project, subject, outfit);

        /// <summary>Give a part the game slots the install answers for, as
        /// <see cref="AuthoredEditSession.EnsurePartSlots"/> does. The install read is the caller's — a
        /// batch is not the place to go to the game — so what it found is passed in, and a part the
        /// install could not answer for is refused by the same sentence.</summary>
        public void EnsurePartSlots(TargetPart target, LegacyResolvedPart? resolved)
        {
            ArgumentNullException.ThrowIfNull(target);
            AuthoredEditSession.EnsurePartSlots(_project, target,
                resolved ?? throw new AuthoredRefusalException(PartNotInstalled));
        }

        /// <summary>Add one content edit to a part, fresh from vanilla. Returns its id, which the rest of
        /// the batch can address immediately.</summary>
        public string CreateEdit(TargetPart target, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            return AuthoredEditSession.CreateEdit(_project, target, label);
        }

        /// <summary>Give a part its hide edit, activated the way any first edit of a part is.</summary>
        public string AddHideEdit(TargetPart target)
        {
            ArgumentNullException.ThrowIfNull(target);
            return AuthoredEditSession.AddHideEdit(_project, target);
        }

        /// <summary>Place an edit in Always.</summary>
        public void PlaceEdit(string editDefinitionId) =>
            AddPlacement(_project.Always, RequiredEdit(_project, editDefinitionId), PlacementNames.Always);

        /// <summary>Store or clear the warning from this compound mesh return on its destination edit.</summary>
        public void SetReturnWarning(string editDefinitionId, string? warning) =>
            RequiredEdit(_project, editDefinitionId).ReturnWarning = NormalizeReturnWarning(warning);

        public void AppendReturnWarning(string editDefinitionId, string warning)
        {
            var edit = RequiredEdit(_project, editDefinitionId);
            string? addition = NormalizeReturnWarning(warning);
            if (addition is null) return;
            edit.ReturnWarning = string.IsNullOrWhiteSpace(edit.ReturnWarning)
                ? addition : edit.ReturnWarning + " " + addition;
        }

        public void ChooseInheritedCarrier(string editDefinitionId, string slotId) =>
            SetBinding(_project, editDefinitionId,
                new Binding { SlotId = slotId, Kind = BindingKind.InheritedLiveCarrier });

        public void ChooseNeutral(string editDefinitionId, string slotId) =>
            SetBinding(_project, editDefinitionId,
                new Binding { SlotId = slotId, Kind = BindingKind.Neutral });

        public void ChooseTargetGameValue(string editDefinitionId, string slotId)
        {
            SetBinding(_project, editDefinitionId,
                new Binding { SlotId = slotId, Kind = BindingKind.TargetGameValue });
            RemoveUnauthoredMaterialValueSlots(_project);
        }

        public void ChooseStructuredValue(string editDefinitionId, string slotId, string label,
            string projectRelativeFile, string semantic, string value,
            string? sourceProjectAssetId = null) =>
            AuthoredEditSession.ChooseStructuredValue(_project, editDefinitionId, slotId, label,
                projectRelativeFile, semantic, value, sourceProjectAssetId);

        /// <summary>Open a transport onto one of the batch's own slots — including an edit this batch has
        /// only just minted, which is the whole reason a return can commit in one piece. The folder it
        /// mints is the batch's, and so are the levels above it that were not there before: a transaction
        /// that refuses takes them all back out, so no folder is left named for an id that never
        /// existed.</summary>
        /// <inheritdoc cref="ProjectAssetIngress.Begin" path="/param[@name='handOver']"/>
        public ProjectAssetIngressSession BeginIngress(string editDefinitionId, string slotId,
            string? unregisteredSource = null, bool handOver = false)
        {
            var ingress = ProjectAssetIngress.Begin(_project, editDefinitionId, slotId, unregisteredSource,
                handOver);
            _files.CreatedDirectory(Path.GetDirectoryName(ingress.ReturnArtifact)!);
            _files.MintedDirectories(ingress.MintedDirectories);
            return ingress;
        }

        /// <summary>Normalize returned bytes into a new project asset and bind the addressed slot, exactly
        /// as <see cref="AuthoredEditSession.PublishAssetForBinding"/> does — but as part of this
        /// transaction rather than one of its own.</summary>
        public ExactAssetPublishResult PublishAssetForBinding(ProjectAssetIngressSession ingress,
            ProjectAssetKind kind, string label, ProjectAssetNormalization normalization,
            ProjectAssetSource? source = null, int? replacementSubmeshCount = null)
        {
            ArgumentNullException.ThrowIfNull(ingress);
            ArgumentNullException.ThrowIfNull(normalization);
            var staged = StagePublish(_project, ingress, kind, label, normalization, source,
                replacementSubmeshCount);
            if (staged is null)
                return new ExactAssetPublishResult(ProjectAssetPublishResult.Unchanged, null, null);
            _files.Staging(staged.Staged);
            return RecordPublish(_project, _files, staged);
        }
    }
}
