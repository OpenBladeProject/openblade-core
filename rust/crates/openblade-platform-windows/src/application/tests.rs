use super::*;
use openblade_application_reporter::{ActivationMode, LEASE_INTERVAL};
use std::cell::Cell;
use std::collections::VecDeque;
use std::rc::Rc;

const APP: &str = r"C:\Games\Game.exe";
const OTHER: &str = r"C:\Elsewhere\Game.exe";
const INSTANCE: Guid = Guid([1; 16]);
const REQUEST: Guid = Guid([2; 16]);
const HWND: usize = 17;

fn nz(value: u32) -> NonZeroU32 {
    NonZeroU32::new(value).unwrap()
}

fn owner() -> Owner {
    Owner {
        thread: nz(8),
        pid: nz(9),
    }
}

#[derive(Clone, Copy)]
enum Step {
    Active(Option<NonZeroU32>),
    Foreground(usize),
    Owner(Option<Owner>),
    Session(Option<NonZeroU32>),
    Open(bool),
    Normalize,
}

struct State {
    alive: Cell<bool>,
    drops: Cell<usize>,
    identity: Option<ProcessIdentity>,
    image: Option<&'static str>,
    cancel_on_image: Option<Arc<AtomicBool>>,
}

impl State {
    fn new(generation: u64, image: &'static str) -> Self {
        Self {
            alive: Cell::new(true),
            drops: Cell::new(0),
            identity: process_identity(owner().pid.get(), generation),
            image: Some(image),
            cancel_on_image: None,
        }
    }
}

struct Handle(Rc<State>);

impl Lifetime for Handle {
    fn identity(&self) -> Option<ProcessIdentity> {
        self.0.identity
    }

    fn image_path(&self) -> Option<String> {
        if let Some(cancel) = &self.0.cancel_on_image {
            cancel.store(true, Ordering::Release);
        }
        self.0.image.map(str::to_owned)
    }

    fn alive(&self) -> bool {
        self.0.alive.get()
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        self.0.drops.set(self.0.drops.get() + 1);
    }
}

#[derive(Default)]
struct Script {
    steps: VecDeque<Step>,
    process: Option<Rc<State>>,
}

impl Script {
    fn frame() -> [Step; 10] {
        [
            Step::Active(Some(nz(2))),
            Step::Foreground(HWND),
            Step::Owner(Some(owner())),
            Step::Session(Some(nz(2))),
            Step::Open(true),
            Step::Normalize,
            Step::Active(Some(nz(2))),
            Step::Owner(Some(owner())),
            Step::Session(Some(nz(2))),
            Step::Foreground(HWND),
        ]
    }

    fn load(&mut self, steps: impl IntoIterator<Item = Step>, process: Rc<State>) {
        assert!(self.steps.is_empty());
        self.steps.extend(steps);
        self.process = Some(process);
    }
}

impl Desktop for Script {
    type Process = Handle;

    fn active_local_session(&mut self) -> Option<NonZeroU32> {
        let Some(Step::Active(result)) = self.steps.pop_front() else {
            panic!("active session")
        };
        result
    }

    fn foreground(&mut self) -> usize {
        let Some(Step::Foreground(result)) = self.steps.pop_front() else {
            panic!("foreground")
        };
        result
    }

    fn owner(&mut self, window: usize) -> Option<Owner> {
        assert_eq!(window, HWND);
        let Some(Step::Owner(result)) = self.steps.pop_front() else {
            panic!("owner")
        };
        result
    }

    fn process_session(&mut self, pid: NonZeroU32) -> Option<NonZeroU32> {
        assert_eq!(pid, owner().pid);
        let Some(Step::Session(result)) = self.steps.pop_front() else {
            panic!("process session")
        };
        result
    }

    fn open_query_and_synchronize(&mut self, pid: NonZeroU32) -> Option<Handle> {
        assert_eq!(pid, owner().pid);
        let Some(Step::Open(allowed)) = self.steps.pop_front() else {
            panic!("open")
        };
        allowed.then(|| Handle(self.process.take().unwrap()))
    }

    fn full_path(&mut self, input: &str) -> Option<String> {
        assert!(matches!(self.steps.pop_front(), Some(Step::Normalize)));
        Some(input.to_owned())
    }

    fn paths_equal(first: &str, second: &str) -> bool {
        path::ordinal_ignore_case(first, second, fixture_upper)
    }
}

fn fixture_upper(c: char) -> u32 {
    // Only the mappings in the synthetic oracle vectors, not a Rust case fold.
    (match c {
        'é' => 'É',
        'σ' | 'ς' => 'Σ',
        'ı' => 'I',
        'ſ' => 'S',
        c => c,
    }) as u32
}

fn binding(mode: ActivationMode) -> Binding<Script> {
    let mut binding = Binding::new(Script::default(), 1, INSTANCE, Arc::default());
    binding.policy.observe_profiles(vec![ApplicationProfile {
        id: Guid([3; 16]),
        executable_path: APP.to_owned(),
        enabled: true,
        mode,
    }]);
    binding
}

fn report(binding: &mut Binding<Script>, elapsed: Duration, path: Option<&str>) {
    let report = binding.poll(elapsed, REQUEST).unwrap();
    assert_eq!(report.executable_path.as_deref(), path);
    binding
        .policy
        .complete(report.sequence, ReportOutcome::Applied);
}

#[test]
fn native_binding_links_without_sampling() {
    let mut native = WindowsApplicationReporter::new(1, INSTANCE, Arc::default());
    std::hint::black_box(
        WindowsApplicationReporter::observe_window as fn(&mut WindowsApplicationReporter, usize),
    );
    native.cancel();
    assert!(native.poll(Duration::ZERO, REQUEST).is_none());
}

#[test]
fn one_handle_lives_through_foreground_loss_and_retires_at_renewal() {
    let mut binding = binding(ActivationMode::WhileRunningAfterActivation);
    let process = Rc::new(State::new(1, r"c:\games\GAME.EXE"));
    binding.api.load(Script::frame(), Rc::clone(&process));
    binding.observe_window(HWND);
    report(&mut binding, Duration::ZERO, Some(APP));
    binding.observe_window(0);
    assert!(binding.poll(Duration::ZERO, REQUEST).is_none());
    assert_eq!(process.drops.get(), 0);
    process.alive.set(false);
    report(&mut binding, LEASE_INTERVAL, None);
    assert_eq!(process.drops.get(), 1);
}

#[test]
fn reused_pid_with_same_basename_never_inherits_prior_lifetime() {
    let mut binding = binding(ActivationMode::WhileRunningAfterActivation);
    let first = Rc::new(State::new(1, APP));
    binding.api.load(Script::frame(), Rc::clone(&first));
    binding.observe_window(HWND);
    report(&mut binding, Duration::ZERO, Some(APP));
    first.alive.set(false);
    let reused = Rc::new(State::new(2, OTHER));
    binding.api.load(Script::frame(), Rc::clone(&reused));
    binding.observe_window(HWND);
    report(&mut binding, Duration::ZERO, None);
    assert_eq!(first.drops.get(), 1);
    binding.observe_window(0);
    assert_eq!(reused.drops.get(), 1);
}

#[test]
fn a_dead_foreground_only_handle_clears_without_a_replacement_window() {
    let mut binding = binding(ActivationMode::Foreground);
    let process = Rc::new(State::new(1, APP));
    binding.api.load(Script::frame(), Rc::clone(&process));
    binding.observe_window(HWND);
    report(&mut binding, Duration::ZERO, Some(APP));
    process.alive.set(false);
    report(&mut binding, Duration::ZERO, None);
    assert_eq!(process.drops.get(), 1);
}

#[test]
fn a_superseded_observation_does_not_activate_a_background_profile() {
    let mut binding = binding(ActivationMode::WhileRunningAfterActivation);
    let first = Rc::new(State::new(1, APP));
    binding.api.load(Script::frame(), Rc::clone(&first));
    binding.observe_window(HWND);
    let in_flight = binding.poll(Duration::ZERO, REQUEST).unwrap();
    binding.observe_window(0);
    let next = Rc::new(State::new(2, APP));
    binding.api.load(Script::frame(), Rc::clone(&next));
    binding.observe_window(HWND);
    assert!(binding.poll(Duration::ZERO, REQUEST).is_none());
    binding.observe_window(0);
    binding
        .policy
        .complete(in_flight.sequence, ReportOutcome::Applied);
    assert!(binding.poll(Duration::ZERO, REQUEST).is_none());
    assert_eq!(first.drops.get(), 0);
    assert_eq!(next.drops.get(), 1);
    binding.cancel();
    assert_eq!(first.drops.get(), 1);
}

#[test]
fn rejects_unconfirmed_window_owner_and_session_before_or_after_open() {
    for (index, failure) in [
        (0, Step::Active(None)),
        (1, Step::Foreground(0)),
        (2, Step::Owner(None)),
        (3, Step::Session(Some(nz(3)))),
        (4, Step::Open(false)),
        (6, Step::Active(Some(nz(3)))),
        (
            7,
            Step::Owner(Some(Owner {
                thread: nz(10),
                ..owner()
            })),
        ),
        (8, Step::Session(None)),
        (9, Step::Foreground(HWND + 1)),
    ] {
        let mut binding = binding(ActivationMode::WhileRunningAfterActivation);
        let process = Rc::new(State::new(1, APP));
        let mut steps = Script::frame();
        steps[index] = failure;
        binding
            .api
            .load(steps.into_iter().take(index + 1), Rc::clone(&process));
        binding.observe_window(HWND);
        assert!(binding.api.steps.is_empty());
        assert!(binding.foreground.is_none());
        assert_eq!(process.drops.get(), usize::from(index > 4));
        assert!(binding.poll(Duration::ZERO, REQUEST).is_none());
    }
}

#[test]
fn rejects_unconfirmed_handle_image_or_liveness_without_reopening() {
    for (process, steps) in [
        (State::new(0, APP), 5),
        (
            State {
                identity: process_identity(11, 1),
                ..State::new(1, APP)
            },
            5,
        ),
        (
            State {
                image: None,
                ..State::new(1, APP)
            },
            5,
        ),
        (State::new(1, r"C:\Windows\applicationFRAMEHOST.EXE"), 6),
        (
            State {
                alive: Cell::new(false),
                ..State::new(1, APP)
            },
            10,
        ),
    ] {
        let mut binding = binding(ActivationMode::Foreground);
        let process = Rc::new(process);
        binding
            .api
            .load(Script::frame().into_iter().take(steps), Rc::clone(&process));
        binding.observe_window(HWND);
        assert!(binding.api.steps.is_empty());
        assert!(binding.foreground.is_none());
        assert_eq!(process.drops.get(), 1);
    }
}

#[test]
fn cancellation_during_image_query_releases_both_sample_and_background_handles() {
    let mut binding = binding(ActivationMode::WhileRunningAfterActivation);
    let first = Rc::new(State::new(1, APP));
    binding.api.load(Script::frame(), Rc::clone(&first));
    binding.observe_window(HWND);
    report(&mut binding, Duration::ZERO, Some(APP));
    let interrupted = Rc::new(State {
        cancel_on_image: Some(Arc::clone(&binding.cancellation)),
        ..State::new(2, OTHER)
    });
    binding
        .api
        .load(Script::frame().into_iter().take(5), Rc::clone(&interrupted));
    binding.observe_window(HWND);
    assert_eq!(first.drops.get(), 1);
    assert_eq!(interrupted.drops.get(), 1);
    binding.observe_window(HWND); // No more native work, even with a nonzero token.
    assert!(binding.poll(LEASE_INTERVAL, REQUEST).is_none());
}

#[test]
fn bounded_utf16_does_not_accept_truncated_or_lossy_paths() {
    assert!(path::decode(&[b'x' as u16, 0], 1).is_some());
    for (buffer, length) in [
        (&[0][..], 0),
        (&[65, 66][..], 1),
        (&[65, 0, 66, 0][..], 3),
        (&[0xd800, 0][..], 1),
        (&[65, 0][..], 2),
    ] {
        assert!(path::decode(buffer, length).is_none());
    }
    let mut longest = vec![65; path::PATH_BUFFER_UNITS];
    longest[path::MAX_PATH_UNITS] = 0;
    assert!(path::decode(&longest, path::MAX_PATH_UNITS).is_some());
    assert!(path::decode(&longest, path::PATH_BUFFER_UNITS).is_none());
}

#[test]
fn windows_path_validation_delegates_only_ordinary_absolute_paths() {
    let regular = " \u{00a0}c:/Games/Temp/../Game.ExE\\\t";
    assert_eq!(
        path::normalize(regular, |input| {
            assert_eq!(input, r"c:/Games/Temp/../Game.ExE\");
            Some(r"c:\Games\Game.ExE\".to_owned())
        })
        .as_deref(),
        Some(r"c:\Games\Game.ExE")
    );
    for extended in [r"\\?\C:\Games\..\Game.exe", r"\\?\UNC\host\share\Game.exe"] {
        assert_eq!(
            path::normalize(extended, |_| panic!("extended")).as_deref(),
            Some(extended)
        );
    }
    let unc = r"\\host\share\Game.exe";
    assert_eq!(
        path::normalize(unc, |v| Some(v.to_owned())).as_deref(),
        Some(unc)
    );
    for rejected in [
        "",
        r"C:Game.exe",
        r"\Game.exe",
        "Game.exe",
        r"\\server\share.exe",
        r"\\.\C:\Game.exe",
        r"\??\C:\Game.exe",
        r"C:\Game.exe:alt.exe",
        r"\\?\C:/Game.exe",
        "C:\\foo\0bar.exe",
        "C:\\foo\u{0085}bar.exe",
    ] {
        assert!(
            path::normalize(rejected, |_| panic!("unqualified")).is_none(),
            "{rejected:?}"
        );
    }
    for rejected in [r"C:\GAMES~1\Game.exe", r"C:\Games\Game.exe.txt", r"C:\"] {
        assert!(path::normalize(APP, |_| Some(rejected.to_owned())).is_none());
    }
    let oversized = format!(r"C:\{}.exe", "𐐨".repeat(path::MAX_PATH_UNITS / 2));
    assert!(path::normalize(&oversized, |_| panic!("too long")).is_none());
}

#[test]
fn ordinal_comparison_does_not_fold_accents_ligatures_or_turkish_i() {
    for (first, second, expected) in [
        (r"C:\Été\Σ𐐀.exe", r"c:\été\ς𐐨.EXE", true),
        ("é", "e\u{0301}", false),
        ("ß", "SS", false),
        ("ß", "ẞ", false),
        ("ﬀ", "ff", false),
        ("i", "ı", false),
        ("I", "İ", false),
        ("S", "ſ", false),
        ("K", "K", false),
        ("I", "i", true),
        // Windows system ICU on the verification host lacks Garay casing.
        ("\u{10D50}", "\u{10D70}", true),
        // Rust's newer Unicode table folds Beria Erfe; .NET 10 does not.
        ("\u{16EA0}", "\u{16EBB}", false),
    ] {
        assert_eq!(
            Script::paths_equal(first, second),
            expected,
            "{first:?}, {second:?}"
        );
    }
}
