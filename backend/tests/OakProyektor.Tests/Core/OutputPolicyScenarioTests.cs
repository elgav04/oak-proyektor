using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

/// <summary>
/// Escenarios con 1, 2, 3 y 4 monitores simulados (<see cref="OutputSimulation"/>). El equipo de pruebas real solo tiene 2: estos tests son la ÚNICA
/// cobertura de 3 o más monitores. Las pruebas «para todo» recorren todas las combinaciones, no una muestra.
/// </summary>
public class OutputPolicyScenarioTests
{
    private static readonly string[] AllIds = OutputSimulation.Ids;
    private static readonly DisplayLostPolicy[] Policies = [DisplayLostPolicy.WarnAndWait, DisplayLostPolicy.MoveToBest];

    private static string[] Take(int n) => [.. AllIds.Take(n)];

    // ───────────── panel arrastrado al monitor de la salida y de vuelta ─────────────

    public static IEnumerable<object[]> PanelDragCases()
    {
        for (var n = 2; n <= 4; n++)
            foreach (var target in Take(n))
                foreach (var start in Take(n).Where(i => i != target))
                    foreach (var away in Take(n).Where(i => i != target))
                        foreach (var policy in Policies)
                            yield return [n, target, start, away, policy];
    }

    [Theory]
    [MemberData(nameof(PanelDragCases))]
    public void Panel_dragged_onto_the_output_monitor_pauses_without_the_output_jumping_and_resumes_by_itself_when_the_panel_leaves(
        int n, string target, string start, string away, DisplayLostPolicy policy)
    {
        var sim = new OutputSimulation(n, target, panel: start, policy);
        sim.Run(10);
        Assert.Equal(0, sim.Transitions);   // estable antes de tocar nada

        sim.MovePanelTo(target);
        sim.Tick();
        Assert.True(sim.Paused);                                     // pausa inmediata: el panel quedaría tapado
        Assert.Equal(OutputPauseReason.PanelCrossed, sim.Reason);

        sim.Run(40);                                                 // mucho tiempo con el panel encima
        Assert.True(sim.Paused);
        Assert.Equal(target, sim.TargetId);                          // NO salta a otro monitor, tampoco con «mover automáticamente»
        Assert.DoesNotContain(sim.Log, l => l.StartsWith("move"));

        sim.MovePanelTo(away);
        sim.Tick();
        Assert.True(sim.Paused);                                     // antirrebote: aún no (un solo sondeo)
        sim.Tick();
        Assert.False(sim.Paused);                                    // ≈ 500 ms estable: reanuda SOLA
        Assert.Equal(target, sim.TargetId);
        Assert.Equal(sim.RectOf(target), sim.WindowRect);            // a pantalla completa, en el rectángulo de su monitor
        Assert.True(sim.Topmost);

        sim.Run(40);
        Assert.Equal(2, sim.Transitions);                            // pausa + reanudación, nada más
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_panel_that_comes_back_after_a_single_poll_does_not_resume_the_output(int n)
    {
        var sim = new OutputSimulation(n, "B", panel: "A");
        sim.MovePanelTo("B");
        sim.Tick();
        Assert.True(sim.Paused);

        sim.MovePanelTo("A");
        sim.Tick();                // un sondeo con el panel fuera…
        sim.MovePanelTo("B");      // …y vuelve
        sim.Run(20);

        Assert.True(sim.Paused);
        Assert.Equal(1, sim.Transitions);
    }

    [Fact]
    public void With_auto_restore_off_a_panel_pause_waits_for_the_operator()
    {
        var sim = new OutputSimulation(3, "B", panel: "A") { AutoRestore = false };
        sim.MovePanelTo("B");
        sim.Tick();
        sim.MovePanelTo("A");
        sim.Run(20);
        Assert.True(sim.Paused);
    }

    // ───────────── pausas del operador ─────────────

    [Theory]
    [InlineData(OutputPauseReason.Manual)]
    [InlineData(OutputPauseReason.Emergency)]
    [InlineData(OutputPauseReason.Unstable)]
    public void Manual_emergency_and_unstable_pauses_never_resume_by_themselves_with_any_number_of_monitors(OutputPauseReason reason)
    {
        for (var n = 2; n <= 4; n++)
            foreach (var policy in Policies)
            {
                var sim = new OutputSimulation(n, "B", panel: "A", policy);
                sim.StartPaused(reason);
                sim.Run(60);
                Assert.True(sim.Paused);
                Assert.Equal(reason, sim.Reason);
                Assert.Equal(0, sim.Transitions);
            }
    }

    // ───────────── monitor preferido que desaparece y vuelve ─────────────

    public static IEnumerable<object[]> TargetAndPanelCases()
    {
        for (var n = 2; n <= 4; n++)
            foreach (var target in Take(n))
                foreach (var panel in Take(n).Where(i => i != target))
                    yield return [n, target, panel];
    }

    [Theory]
    [MemberData(nameof(TargetAndPanelCases))]
    public void Warn_and_wait_pauses_when_the_preferred_monitor_disappears_never_moves_and_resumes_when_it_returns_even_with_another_resolution(int n, string target, string panel)
    {
        var sim = new OutputSimulation(n, target, panel);
        var all = Take(n);

        sim.SetConnected([.. all.Where(i => i != target)]);
        sim.Tick();
        Assert.False(sim.Paused);                    // antirrebote: un sondeo no basta (un parpadeo de enumeración no deja la proyección en negro)
        sim.Tick();
        Assert.Equal((true, OutputPauseReason.DisplayLost), (sim.Paused, sim.Reason));

        sim.Run(40);                                 // aunque haya otros monitores libres, NO salta por su cuenta
        Assert.Equal(target, sim.TargetId);
        Assert.DoesNotContain(sim.Log, l => l.StartsWith("move"));

        // Vuelve el MISMO monitor (mismo ID) con otra resolución.
        var old = sim.RectOf(target);
        var smaller = new ScreenRect(old.X, old.Y, 1280, 720);
        sim.SetRect(target, smaller);
        sim.SetConnected(all);
        sim.Tick();
        Assert.True(sim.Paused);
        sim.Tick();
        Assert.False(sim.Paused);
        Assert.Equal(smaller, sim.WindowRect);       // reanuda en el rectángulo NUEVO
        Assert.Equal(2, sim.Transitions);
    }

    [Fact]
    public void A_one_poll_blink_of_the_monitor_list_never_pauses_with_either_policy()
    {
        foreach (var policy in Policies)
            for (var n = 2; n <= 4; n++)
            {
                var sim = new OutputSimulation(n, "B", panel: "A", policy);
                for (var i = 0; i < 60; i++)
                {
                    sim.SetConnected([.. Take(n).Where(id => i % 2 == 0 || id != "B")]);   // B aparece y desaparece en cada sondeo
                    sim.Tick();
                }
                Assert.Equal(0, sim.Transitions);
                Assert.False(sim.Paused);
            }
    }

    // ───────────── monitor preferido que desaparece y aparece uno nuevo (las dos políticas) ─────────────

    [Fact]
    public void Warn_and_wait_a_new_monitor_appearing_while_the_preferred_is_gone_changes_nothing_by_itself()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");        // A (panel) + B (preferido)
        sim.SetConnected("A");
        sim.Run(3);
        Assert.Equal(OutputPauseReason.DisplayLost, sim.Reason);

        sim.SetConnected("A", "C");                                // aparece C (otro proyector)
        sim.Run(40);

        Assert.True(sim.Paused);
        Assert.Equal("B", sim.TargetId);
        Assert.DoesNotContain(sim.Log, l => l.StartsWith("move"));
        Assert.Equal(1, sim.Transitions);
    }

    [Fact]
    public void Move_to_best_with_only_the_panel_left_pauses_then_moves_to_the_new_monitor_and_goes_back_to_the_preferred()
    {
        var sim = new OutputSimulation(2, "B", panel: "A", DisplayLostPolicy.MoveToBest);
        sim.SetConnected("A");
        sim.Run(3);
        Assert.Equal((true, OutputPauseReason.DisplayLost), (sim.Paused, sim.Reason));   // no hay a dónde ir sin tapar el panel

        sim.SetConnected("A", "C");
        sim.Run(3);
        Assert.False(sim.Paused);
        Assert.Equal("C", sim.TargetId);
        Assert.Equal(sim.RectOf("C"), sim.WindowRect);

        sim.SetConnected("A", "B", "C");                           // vuelve el preferido: la salida siempre intenta volver a él
        sim.Run(3);
        Assert.Equal("B", sim.TargetId);
        Assert.Equal(sim.RectOf("B"), sim.WindowRect);
        Assert.False(sim.Paused);
        Assert.Equal(3, sim.Transitions);                          // pausa, mover a C, mover a B
    }

    [Fact]
    public void Move_to_best_with_a_spare_monitor_moves_without_pausing_and_returns_to_the_preferred()
    {
        var sim = new OutputSimulation(3, "B", panel: "A", DisplayLostPolicy.MoveToBest);   // A panel, B preferido, C libre
        sim.SetConnected("A", "C");
        sim.Run(3);

        Assert.Equal(["move:C"], sim.Log);                         // ni siquiera pasa por pausa
        Assert.Equal("C", sim.TargetId);

        sim.SetConnected("A", "B", "C");
        sim.Run(3);
        Assert.Equal("B", sim.TargetId);
        Assert.Equal(["move:C", "move:B"], sim.Log);
    }

    [Fact]
    public void The_preferred_monitor_returning_under_the_panel_does_not_pull_the_output_onto_the_panel()
    {
        var sim = new OutputSimulation(3, "B", panel: "A", DisplayLostPolicy.MoveToBest);
        sim.SetConnected("A", "C");
        sim.Run(3);
        Assert.Equal("C", sim.TargetId);

        sim.MovePanelTo("B");                                      // el panel se pone justo en el monitor que vuelve
        sim.SetConnected("A", "B", "C");
        sim.Run(40);
        Assert.Equal("C", sim.TargetId);                           // no va al monitor del panel
        Assert.False(sim.Paused);

        sim.MovePanelTo("A");                                      // el panel sale: ahora sí vuelve al preferido
        sim.Run(3);
        Assert.Equal("B", sim.TargetId);
    }

    [Fact]
    public void An_explicit_choice_becomes_the_preferred_one_so_a_fallback_never_sticks()
    {
        // Elegido C a mano (preferido C), cae a otro y C vuelve: regresa a C.
        var sim = new OutputSimulation(4, "C", panel: "A", DisplayLostPolicy.MoveToBest);
        sim.SetConnected("A", "B", "D");
        sim.Run(3);
        Assert.NotEqual("C", sim.TargetId);
        sim.SetConnected("A", "B", "C", "D");
        sim.Run(3);
        Assert.Equal("C", sim.TargetId);
    }

    // ───────────── elección del mejor monitor: regla documentada ─────────────

    [Fact]
    public void The_best_display_rule_with_two_three_and_four_monitors()
    {
        DisplayInfo D(string id, int x, bool primary, bool panel) => new(id, x, 0, 1920, 1080, 1, primary, panel);
        var a = D("A", 0, true, true); var b = D("B", 1920, false, false); var c = D("C", 3840, false, false); var d = D("D", 5760, false, false);

        Assert.Null(BestDisplayPicker.Choose([a], []));                                   // 1 monitor (el del panel): no hay candidato
        Assert.Equal("B", BestDisplayPicker.Choose([a, b], [])!.Id);                       // 2: el único que no es el del panel
        Assert.Null(BestDisplayPicker.Choose([a, b], [], excludeId: "B"));                 // 2, y el único candidato es el que desapareció
        Assert.Equal("D", BestDisplayPicker.Choose([a, b, c, d], [])!.Id);                 // 4: no principal y más a la derecha
        Assert.Equal("C", BestDisplayPicker.Choose([a, b, c, d], ["C", "B"])!.Id);         // el último usado gana
        Assert.Equal("B", BestDisplayPicker.Choose([a, b, c, d], ["A", "B"])!.Id);         // el último usado, pero A es el panel: se salta a B
        Assert.Equal("C", BestDisplayPicker.Choose([a, b, c, d], ["D"], excludeId: "D")!.Id);   // D excluido: la regla sigue con el resto
    }

    [Fact]
    public void The_primary_monitor_is_chosen_only_when_it_is_the_only_free_one()
    {
        // El panel está en B; A es el principal y C el no principal.
        DisplayInfo D(string id, int x, bool primary, bool panel) => new(id, x, 0, 1920, 1080, 1, primary, panel);
        var a = D("A", 0, true, false); var b = D("B", 1920, false, true); var c = D("C", 3840, false, false);
        Assert.Equal("C", BestDisplayPicker.Choose([a, b, c], [])!.Id);
        Assert.Equal("A", BestDisplayPicker.Choose([a, b], [])!.Id);
    }

    [Fact]
    public void Ties_are_broken_by_the_rightmost_monitor_then_the_topmost_and_the_choice_is_deterministic()
    {
        DisplayInfo D(string id, int x, int y) => new(id, x, y, 1920, 1080, 1, false, false);
        var panel = new DisplayInfo("P", 0, 0, 1920, 1080, 1, true, true);
        Assert.Equal("R", BestDisplayPicker.Choose([panel, D("L", 1920, 0), D("R", 3840, 0)], [])!.Id);
        Assert.Equal("Up", BestDisplayPicker.Choose([panel, D("Down", 1920, 1080), D("Up", 1920, 0)], [])!.Id);   // misma X: el de arriba
        Assert.Equal("a", BestDisplayPicker.Choose([panel, D("b", 1920, 0), D("a", 1920, 0)], [])!.Id);           // empate total: por ID
    }

    [Fact]
    public void The_picker_never_returns_the_panel_or_the_excluded_one_and_ignores_the_list_order_for_every_combination()
    {
        var rects = Enumerable.Range(0, 4).Select(i => (Id: AllIds[i], X: i * 1920, Primary: i == 0)).ToArray();
        var recents = new List<string[]>
        {
            Array.Empty<string>(), new[] { "A" }, new[] { "B" }, new[] { "C" }, new[] { "D" }, new[] { "D", "C", "B", "A" }, new[] { "A", "B", "C", "D" },
        };

        for (var mask = 1; mask < 16; mask++)
        {
            var present = rects.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            foreach (var panelId in present.Select(p => p.Id).Cast<string?>().Append(null))
                foreach (var exclude in AllIds.Cast<string?>().Append(null))
                    foreach (var recent in recents)
                    {
                        var list = present.Select(p => new DisplayInfo(p.Id, p.X, 0, 1920, 1080, 1, p.Primary, p.Id == panelId)).ToList();
                        var chosen = BestDisplayPicker.Choose(list, recent, exclude);
                        var candidates = list.Where(d => !d.HostsPanel && d.Id != exclude).ToList();

                        if (candidates.Count == 0) { Assert.Null(chosen); continue; }
                        Assert.NotNull(chosen);
                        Assert.False(chosen.HostsPanel);
                        Assert.NotEqual(exclude, chosen.Id);
                        Assert.Contains(chosen, list);

                        var lastUsed = recent.FirstOrDefault(r => candidates.Any(c => c.Id == r));
                        if (lastUsed is not null) Assert.Equal(lastUsed, chosen.Id);
                        else if (candidates.Any(c => !c.IsPrimary)) Assert.Equal(candidates.Where(c => !c.IsPrimary).Max(c => c.X), chosen.X);

                        var reversed = BestDisplayPicker.Choose([.. list.AsEnumerable().Reverse()], recent, exclude);
                        Assert.Equal(chosen.Id, reversed!.Id);
                    }
        }
    }

    // ───────────── Duplicar y Extender ─────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Duplicate_mode_pauses_at_once_with_its_own_reason_and_extend_resumes_by_itself(int n)
    {
        foreach (var policy in Policies)
        {
            var sim = new OutputSimulation(n, "B", panel: "A", policy);
            sim.Run(5);

            sim.Duplicate = true;                      // Win+P → Duplicar: Windows deja un solo monitor lógico (el del panel)
            sim.SetConnected("A");
            sim.Tick();
            Assert.Equal((true, OutputPauseReason.DuplicateMode), (sim.Paused, sim.Reason));

            sim.Run(60);
            Assert.Equal((true, OutputPauseReason.DuplicateMode), (sim.Paused, sim.Reason));
            Assert.DoesNotContain(sim.Log, l => l.StartsWith("move"));      // con «mover automáticamente» tampoco: no hay a dónde ir

            sim.Duplicate = false;                     // Win+P → Extender
            sim.SetConnected(Take(n));
            sim.Tick();
            Assert.True(sim.Paused);                   // estable ~500 ms
            sim.Tick();
            Assert.False(sim.Paused);
            Assert.Equal("B", sim.TargetId);
            Assert.Equal(sim.RectOf("B"), sim.WindowRect);
            Assert.Equal(2, sim.Transitions);
        }
    }

    [Fact]
    public void Duplicate_mode_is_detected_even_if_the_projector_is_still_listed()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        sim.Duplicate = true;
        sim.Tick();
        Assert.Equal(OutputPauseReason.DuplicateMode, sim.Reason);
    }

    [Fact]
    public void Duplicate_mode_replaces_an_automatic_pause_reason_so_the_notice_names_the_real_cause_and_extend_still_resumes()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        sim.SetConnected("A");
        sim.Run(3);
        Assert.Equal(OutputPauseReason.DisplayLost, sim.Reason);

        sim.Duplicate = true;
        sim.Tick();
        Assert.Equal(OutputPauseReason.DuplicateMode, sim.Reason);

        sim.Duplicate = false;
        sim.SetConnected("A", "B");
        sim.Run(3);
        Assert.False(sim.Paused);
    }

    [Theory]
    [InlineData(OutputPauseReason.Manual)]
    [InlineData(OutputPauseReason.Emergency)]
    [InlineData(OutputPauseReason.Unstable)]
    public void Duplicate_mode_never_overrides_or_clears_an_operator_pause(OutputPauseReason reason)
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        sim.StartPaused(reason);
        sim.Duplicate = true;
        sim.SetConnected("A");
        sim.Run(10);
        Assert.Equal(reason, sim.Reason);

        sim.Duplicate = false;
        sim.SetConnected("A", "B");
        sim.Run(20);
        Assert.Equal((true, reason), (sim.Paused, sim.Reason));   // al volver a Extender sigue esperando al operador
    }

    [Fact]
    public void Extend_that_brings_the_projector_back_with_another_resolution_resumes_in_the_new_rectangle()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        sim.Duplicate = true; sim.SetConnected("A"); sim.Tick();
        sim.Duplicate = false;
        var smaller = new ScreenRect(1920, 0, 1280, 720);
        sim.SetRect("B", smaller);
        sim.SetConnected("A", "B");
        sim.Run(3);
        Assert.Equal(smaller, sim.WindowRect);
        Assert.False(sim.Paused);
    }

    [Fact]
    public void Duplicate_mode_pure_detection_flags_two_active_paths_sharing_a_source()
    {
        Assert.False(DisplayTopology.IsDuplicate([]));
        Assert.False(DisplayTopology.IsDuplicate(["src1"]));                  // un solo monitor
        Assert.False(DisplayTopology.IsDuplicate(["src1", "src2"]));          // Extender: fuentes distintas
        Assert.True(DisplayTopology.IsDuplicate(["src1", "src1"]));           // Duplicar
        Assert.True(DisplayTopology.IsDuplicate(["src1", "src2", "src1"]));   // 3 pantallas, dos duplicadas
        Assert.True(DisplayTopology.IsDuplicate(["src1", "src1", "src1"]));
    }

    // ───────────── la salida NUNCA elige el monitor del panel (todas las combinaciones) ─────────────

    [Fact]
    public void The_policy_never_targets_the_panel_monitor_nor_acts_when_it_must_not_for_every_combination()
    {
        var reasons = new OutputPauseReason?[] { null, OutputPauseReason.DisplayLost, OutputPauseReason.PanelCrossed, OutputPauseReason.DuplicateMode,
            OutputPauseReason.Manual, OutputPauseReason.Emergency, OutputPauseReason.Unstable };
        var rects = Enumerable.Range(0, 4).Select(i => new ScreenRect(i * 1920, 0, 1920, 1080)).ToArray();
        var outputs = new[] { new ScreenRect(1920, 0, 1920, 1080), new ScreenRect(500, 0, 1920, 1080), OutputSimulation.Minimized };
        var checkedCases = 0;

        for (var mask = 1; mask < 16; mask++)
        {
            var present = Enumerable.Range(0, 4).Where(i => (mask & (1 << i)) != 0).ToArray();
            foreach (var panelIndex in present.Cast<int?>().Append(null))
                foreach (var targetId in AllIds)
                    foreach (var preferred in AllIds.Cast<string?>().Append(null))
                        foreach (var policy in Policies)
                            foreach (var reason in reasons)
                                foreach (var duplicate in new[] { false, true })
                                    foreach (var applied in new ScreenRect?[] { null, new ScreenRect(0, 0, 640, 480) })
                                        foreach (var output in outputs)
                                        {
                                            var list = present.Select(i => new DisplayInfo(AllIds[i], rects[i].X, 0, 1920, 1080, 1, i == 0, i == panelIndex)).ToList();
                                            var paused = reason is not null;
                                            var input = new OutputSafetyInput(output, !paused, list, targetId, paused, reason, true, applied, preferred, policy, ["D", "C"], duplicate);
                                            var d = OutputSafetyPolicy.Evaluate(input);
                                            checkedCases++;

                                            var target = list.FirstOrDefault(x => x.Id == targetId);
                                            switch (d.Kind)
                                            {
                                                case SafetyActionKind.Move:
                                                    var dest = list.SingleOrDefault(x => x.Id == d.DisplayId);
                                                    Assert.NotNull(dest);
                                                    Assert.False(dest.HostsPanel);                    // JAMÁS el monitor del panel
                                                    Assert.Equal(ScreenRect.Of(dest), d.Area);
                                                    Assert.False(duplicate);
                                                    break;
                                                case SafetyActionKind.Resume:
                                                    Assert.NotNull(target);
                                                    Assert.False(target.HostsPanel);
                                                    Assert.False(duplicate);
                                                    Assert.True(paused && OutputPauseState.IsAutomatic(reason!.Value));
                                                    break;
                                                case SafetyActionKind.Refit:
                                                    Assert.NotNull(target);
                                                    Assert.False(target.HostsPanel);
                                                    Assert.Equal(ScreenRect.Of(target), d.Area);
                                                    Assert.False(paused);
                                                    break;
                                            }
                                            // Una pausa del operador o inestable no la levanta ni la mueve la política.
                                            if (reason is OutputPauseReason.Manual or OutputPauseReason.Emergency or OutputPauseReason.Unstable)
                                                Assert.Equal(SafetyActionKind.None, d.Kind);
                                            // Con el panel sobre el monitor de la salida no hay reanudación (taparía al operador).
                                            if (!paused && target is { HostsPanel: true } && !duplicate)
                                                Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
                                        }
        }
        Assert.True(checkedCases > 50_000);   // se recorrieron de verdad
    }

    // ───────────── sin oscilación ─────────────

    [Fact]
    public void A_stable_arrangement_never_produces_a_transition_for_every_layout()
    {
        for (var n = 1; n <= 4; n++)
            foreach (var target in Take(n))
                foreach (var panel in Take(n).Where(p => p != target || n == 1).Take(n))
                {
                    if (panel == target) continue;   // con el panel sobre el monitor de la salida sí hay (una) pausa: se prueba arriba
                    foreach (var policy in Policies)
                    {
                        var sim = new OutputSimulation(n, target, panel, policy);
                        sim.Run(120);
                        Assert.Equal(0, sim.Transitions);
                        Assert.False(sim.Paused);
                    }
                }
    }

    [Fact]
    public void A_resolution_change_refits_once_and_never_oscillates()
    {
        var sim = new OutputSimulation(3, "B", panel: "A");
        sim.SetRect("B", new ScreenRect(1920, 0, 1280, 720));
        sim.Run(60);
        Assert.Equal(["refit"], sim.Log);
        Assert.False(sim.Paused);
    }

    [Fact]
    public void A_monitor_that_flaps_every_two_polls_trips_the_limit_and_stays_paused_with_a_warning()
    {
        foreach (var policy in Policies)
        {
            var sim = new OutputSimulation(2, "B", panel: "A", policy);
            for (var i = 0; i < 100; i++)
            {
                sim.SetConnected((i / 2) % 2 == 0 ? ["A", "B"] : ["A"]);   // 2 sondeos presente, 2 ausente…
                sim.Tick();
            }

            Assert.Equal((true, OutputPauseReason.Unstable), (sim.Paused, sim.Reason));
            Assert.InRange(sim.Transitions, 1, 6);                       // se detuvo en el tope; no siguió oscilando 100 sondeos
            Assert.Equal(1, sim.Log.Count(l => l == "pause:Unstable"));
        }
    }

    [Fact]
    public void A_panel_that_flaps_on_and_off_the_output_monitor_trips_the_limit_too()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        for (var i = 0; i < 100; i++)
        {
            sim.MovePanelTo((i / 2) % 2 == 0 ? "A" : "B");
            sim.Tick();
        }
        Assert.Equal((true, OutputPauseReason.Unstable), (sim.Paused, sim.Reason));
        Assert.InRange(sim.Transitions, 1, 6);
    }

    [Fact]
    public void After_the_limit_only_the_operator_resumes_and_then_counting_starts_over()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        for (var i = 0; i < 40; i++)
        {
            sim.SetConnected((i / 2) % 2 == 0 ? ["A", "B"] : ["A"]);
            sim.Tick();
        }
        Assert.Equal(OutputPauseReason.Unstable, sim.Reason);

        sim.SetConnected("A", "B");               // el monitor se estabiliza: aun así NO se reanuda sola
        sim.Run(60);
        Assert.Equal((true, OutputPauseReason.Unstable), (sim.Paused, sim.Reason));

        sim.OperatorResume();
        var before = sim.Transitions;
        sim.Run(60);
        Assert.False(sim.Paused);
        Assert.Equal(before, sim.Transitions);

        sim.MovePanelTo("B"); sim.Tick();         // una pausa normal vuelve a funcionar tras reanudar
        Assert.Equal(OutputPauseReason.PanelCrossed, sim.Reason);
    }

    [Fact]
    public void Slow_legitimate_back_and_forth_never_trips_the_limit()
    {
        var sim = new OutputSimulation(2, "B", panel: "A");
        for (var cycle = 0; cycle < 10; cycle++)
        {
            sim.MovePanelTo("B"); sim.Run(2);      // pausa
            sim.MovePanelTo("A"); sim.Run(2);      // reanuda
            sim.Run(40);                           // 20 s de calma: la ventana de 10 s se vacía
        }
        Assert.False(sim.Paused);
        Assert.DoesNotContain(sim.Log, l => l == "pause:Unstable");
        Assert.Equal(20, sim.Transitions);
    }

    // ───────────── piezas pequeñas ─────────────

    [Fact]
    public void The_limiter_trips_at_the_cap_inside_the_window_and_forgets_old_transitions()
    {
        var limiter = new TransitionLimiter(6, TimeSpan.FromSeconds(10));
        var t0 = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++) Assert.False(limiter.Record(t0.AddSeconds(i)));
        Assert.True(limiter.Record(t0.AddSeconds(5)));                       // la 6ª en 10 s

        limiter.Clear();
        for (var i = 0; i < 5; i++) Assert.False(limiter.Record(t0.AddSeconds(i * 3)));   // una cada 3 s: nunca 6 en 10 s
        Assert.False(limiter.Record(t0.AddSeconds(15)));
    }

    [Fact]
    public void The_stabilizer_needs_the_same_decision_twice_in_a_row()
    {
        var s = new DecisionStabilizer(2);
        var move = new OutputSafetyDecision(SafetyActionKind.Move, DisplayId: "C", Area: new ScreenRect(3840, 0, 1920, 1080));
        var other = move with { DisplayId = "D" };
        Assert.False(s.Observe(move));
        Assert.False(s.Observe(other));    // otra decisión: reinicia
        Assert.True(s.Observe(other));
        Assert.False(s.Observe(null));
        Assert.False(s.Observe(other));
    }

    [Fact]
    public void The_automatic_open_prefers_the_preferred_monitor_unless_it_hosts_the_panel()
    {
        DisplayInfo D(string id, int x, bool panel) => new(id, x, 0, 1920, 1080, 1, x == 0, panel);
        var list = new[] { D("A", 0, true), D("B", 1920, false), D("C", 3840, false) };

        Assert.Equal("C", DisplayResolver.Resolve(list, null, "C").Display!.Id);       // el preferido, no «el primero»
        Assert.Equal("B", DisplayResolver.Resolve(list, null, null).Display!.Id);      // sin preferido: el primero que no es el panel
        Assert.Equal("B", DisplayResolver.Resolve(list, null, "A").Display!.Id);       // el preferido aloja el panel: no se usa
        Assert.Equal("B", DisplayResolver.Resolve(list, null, "Z").Display!.Id);       // el preferido no está conectado
        Assert.Equal(DisplayResolutionKind.NotFound, DisplayResolver.Resolve(list, "Z", "C").Kind);   // un ID explícito inexistente sigue siendo un error
        Assert.Null(DisplayResolver.Resolve([D("A", 0, true)], null, "A").Display);    // 1 monitor: ventana normal
    }

    [Fact]
    public void Pause_state_priorities_with_the_new_reasons()
    {
        var p = new OutputPauseState();
        p.RequestPause(OutputPauseReason.DisplayLost);
        Assert.True(p.RequestPause(OutputPauseReason.DuplicateMode).Changed);         // Duplicar es la causa real: sustituye
        Assert.Equal(OutputPauseReason.DuplicateMode, p.Reason);
        Assert.False(p.RequestPause(OutputPauseReason.PanelCrossed).Changed);         // un automático no sustituye a otro
        Assert.True(p.RequestPause(OutputPauseReason.Unstable).Changed);              // sube de nivel
        Assert.False(p.TryAutoResume(OutputPauseReason.Unstable));                    // inestable no se reanuda solo
        Assert.False(p.RequestPause(OutputPauseReason.DuplicateMode).Changed);        // nunca se rebaja
        Assert.True(p.RequestPause(OutputPauseReason.Emergency, order: 5).Changed);
        Assert.Equal(OutputPauseReason.Emergency, p.Reason);
        Assert.True(p.IsSupersededBy(4));                                             // la emergencia sí es pausa del operador…

        var q = new OutputPauseState();
        q.RequestPause(OutputPauseReason.Unstable);
        Assert.False(q.IsSupersededBy(0));                                            // …pero Unstable no cuenta como orden del operador
        Assert.Equal(OperatorResumeResult.Resumed, q.TryOperatorResume(1));           // y el operador la levanta
    }
}
