// Included by build.rs, which emits the `.obid` section, and by
// native_package.rs, whose readers admit it, so the two cannot differ. The
// managed `NativeArtifact.MaximumIdentityBytes` states the same value, and
// native_package::tests pins it.
//
// The sorted Cargo feature list is most of the record and grows with every
// Agent capability: the worst shippable composition was 977 bytes when this
// bound was raised from 1024.
/// Largest compiled build identity record (`.obid` section), in bytes.
const MAX_IDENTITY_BYTES: usize = 4096;
