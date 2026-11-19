// A module of both build.rs, which emits the `.obid` section, and
// native_package.rs, whose readers admit it, so the two cannot differ. Being
// a module rather than an `include!`, it is also reached by `cargo fmt`. The
// managed `NativeArtifact.MaximumIdentityBytes` states the same value, and
// native_package::tests pins it.
//
// The sorted Cargo feature list is most of the record and grows with every
// Agent capability: the worst shippable composition was 977 bytes when this
// bound was raised from 1024.
/// Largest compiled build identity record (`.obid` section), in bytes.
pub(super) const MAX_IDENTITY_BYTES: usize = 4096;
