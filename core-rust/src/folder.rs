//! Safe deterministic folder comparison. Only unambiguous byte-identical file moves
//! are called renames. AST and semantic equivalence are separate future layers.
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    hash::{DefaultHasher, Hasher},
    io::{self, Read},
    path::{Path, PathBuf},
};

const MAX_FILES: usize = 20_000;
const MAX_FILE_BYTES: u64 = 40 * 1024 * 1024;
const EXCLUDED_DIRS: &[&str] = &[
    ".git",
    "target",
    "node_modules",
    "bin",
    "obj",
    ".vs",
    ".idea",
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FileChange {
    Added(String),
    Deleted(String),
    Modified(String),
    Renamed { before: String, after: String },
}

fn gather(root: &Path) -> io::Result<BTreeMap<String, PathBuf>> {
    let root = root.canonicalize()?;
    if !root.is_dir() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "expected directory",
        ));
    }
    let mut files = BTreeMap::new();
    let mut pending = vec![root.clone()];
    while let Some(dir) = pending.pop() {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let name = entry.file_name();
            let name = name.to_string_lossy();
            let kind = entry.file_type()?;
            if kind.is_symlink() {
                continue;
            } // never walk outside the project
            if kind.is_dir() {
                if !EXCLUDED_DIRS.contains(&name.as_ref()) {
                    pending.push(entry.path());
                }
            } else if kind.is_file() {
                let meta = entry.metadata()?;
                if meta.len() > MAX_FILE_BYTES {
                    return Err(io::Error::other("file size limit exceeded"));
                }
                let relative = entry
                    .path()
                    .strip_prefix(&root)
                    .map_err(io::Error::other)?
                    .to_string_lossy()
                    .replace('\\', "/");
                files.insert(relative, entry.path());
                if files.len() > MAX_FILES {
                    return Err(io::Error::other("file count limit exceeded"));
                }
            }
        }
    }
    Ok(files)
}

fn fingerprint(path: &Path) -> io::Result<(u64, u64)> {
    let mut reader = fs::File::open(path)?;
    let mut hash = DefaultHasher::new();
    let mut length = 0_u64;
    let mut buf = [0_u8; 65536];
    loop {
        let n = reader.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hash.write(&buf[..n]);
        length += n as u64;
    }
    Ok((length, hash.finish()))
}

fn identical(a: &Path, b: &Path) -> io::Result<bool> {
    if fs::metadata(a)?.len() != fs::metadata(b)?.len() {
        return Ok(false);
    }
    let mut a = fs::File::open(a)?;
    let mut b = fs::File::open(b)?;
    let (mut ab, mut bb) = ([0_u8; 65536], [0_u8; 65536]);
    loop {
        let na = a.read(&mut ab)?;
        let nb = b.read(&mut bb)?;
        if na != nb || ab[..na] != bb[..nb] {
            return Ok(false);
        }
        if na == 0 {
            return Ok(true);
        }
    }
}

pub fn compare_directories(before: &Path, after: &Path) -> io::Result<Vec<FileChange>> {
    let left = gather(before)?;
    let right = gather(after)?;
    let lkeys: BTreeSet<_> = left.keys().cloned().collect();
    let rkeys: BTreeSet<_> = right.keys().cloned().collect();
    let mut changes: Vec<FileChange> = Vec::new();
    for name in lkeys.intersection(&rkeys) {
        if !identical(&left[name], &right[name])? {
            changes.push(FileChange::Modified(name.clone()));
        }
    }
    let deleted: Vec<_> = lkeys.difference(&rkeys).cloned().collect();
    let added: Vec<_> = rkeys.difference(&lkeys).cloned().collect();
    // Rename only on a unique exact-byte match on BOTH sides. Repeated identical
    // files are ambiguous and remain separate additions/deletions.
    let left_hashes: Vec<_> = deleted
        .iter()
        .map(|p| fingerprint(&left[p]))
        .collect::<io::Result<_>>()?;
    let right_hashes: Vec<_> = added
        .iter()
        .map(|p| fingerprint(&right[p]))
        .collect::<io::Result<_>>()?;
    // Index signatures once. The earlier nested scans were quadratic for 20K
    // unmatched files even when every candidate had different content.
    let mut left_groups: BTreeMap<(u64, u64), Vec<usize>> = BTreeMap::new();
    let mut right_groups: BTreeMap<(u64, u64), Vec<usize>> = BTreeMap::new();
    for (i, signature) in left_hashes.into_iter().enumerate() {
        left_groups.entry(signature).or_default().push(i);
    }
    for (j, signature) in right_hashes.into_iter().enumerate() {
        right_groups.entry(signature).or_default().push(j);
    }

    let mut used_l = BTreeSet::new();
    let mut used_r = BTreeSet::new();
    for (signature, left_candidates) in &left_groups {
        if left_candidates.len() != 1 {
            continue;
        }
        let Some(right_candidates) = right_groups.get(signature) else {
            continue;
        };
        if right_candidates.len() != 1 {
            continue;
        }
        let i = left_candidates[0];
        let j = right_candidates[0];
        // Fingerprints are never proof of equality; compare the bytes in full.
        if !identical(&left[&deleted[i]], &right[&added[j]])? {
            continue;
        }
        used_l.insert(i);
        used_r.insert(j);
        changes.push(FileChange::Renamed {
            before: deleted[i].clone(),
            after: added[j].clone(),
        });
    }
    for (i, p) in deleted.iter().enumerate() {
        if !used_l.contains(&i) {
            changes.push(FileChange::Deleted(p.clone()));
        }
    }
    for (j, p) in added.iter().enumerate() {
        if !used_r.contains(&j) {
            changes.push(FileChange::Added(p.clone()));
        }
    }
    changes.sort_by_key(|x| match x {
        FileChange::Added(p) | FileChange::Deleted(p) | FileChange::Modified(p) => p.clone(),
        FileChange::Renamed { after, .. } => after.clone(),
    });
    Ok(changes)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn dirs() -> (PathBuf, PathBuf) {
        let parent = std::env::temp_dir().join(format!(
            "dc-folder-test-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = fs::remove_dir_all(&parent);
        let left = parent.join("a");
        let right = parent.join("b");
        fs::create_dir_all(&left).unwrap();
        fs::create_dir_all(&right).unwrap();
        (left, right)
    }
    #[test]
    fn detects_modify_and_rename() {
        let (a, b) = dirs();
        fs::write(a.join("old.rs"), "fn a() {}").unwrap();
        fs::write(b.join("new.rs"), "fn a() {}").unwrap();
        fs::write(a.join("keep.py"), "x=1").unwrap();
        fs::write(b.join("keep.py"), "x=2").unwrap();
        assert_eq!(
            compare_directories(&a, &b).unwrap(),
            vec![
                FileChange::Modified("keep.py".into()),
                FileChange::Renamed {
                    before: "old.rs".into(),
                    after: "new.rs".into()
                }
            ]
        );
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }
    #[test]
    fn ambiguous_duplicates_are_not_renames() {
        let (a, b) = dirs();
        fs::write(a.join("a.txt"), "duplicate").unwrap();
        fs::write(a.join("b.txt"), "duplicate").unwrap();
        fs::write(b.join("c.txt"), "duplicate").unwrap();
        let result = compare_directories(&a, &b).unwrap();
        assert_eq!(result.len(), 3);
        assert!(
            !result
                .iter()
                .any(|x| matches!(x, FileChange::Renamed { .. }))
        );
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }
    #[test]
    fn ignores_git_and_build_folders() {
        let (a, b) = dirs();
        fs::create_dir_all(b.join(".git")).unwrap();
        fs::create_dir_all(b.join("target")).unwrap();
        fs::write(b.join(".git/config"), "config").unwrap();
        fs::write(b.join("target/build"), "build").unwrap();
        assert!(compare_directories(&a, &b).unwrap().is_empty());
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }
    #[test]
    fn added_deleted_modified_and_unicode_move() {
        let (a, b) = dirs();
        fs::create_dir_all(a.join("소스")).unwrap();
        fs::create_dir_all(b.join("새폴더")).unwrap();
        fs::write(a.join("소스/옛이름.rs"), "fn 인사() {}\n").unwrap();
        fs::write(b.join("새폴더/새이름.rs"), "fn 인사() {}\n").unwrap();
        fs::write(a.join("remove.py"), "x=1").unwrap();
        fs::write(b.join("added.ts"), "let x=1").unwrap();
        fs::write(a.join("same.cs"), "int n = 1;").unwrap();
        fs::write(b.join("same.cs"), "int n = 2;").unwrap();
        assert_eq!(
            compare_directories(&a, &b).unwrap(),
            vec![
                FileChange::Added("added.ts".into()),
                FileChange::Deleted("remove.py".into()),
                FileChange::Modified("same.cs".into()),
                FileChange::Renamed {
                    before: "소스/옛이름.rs".into(),
                    after: "새폴더/새이름.rs".into()
                },
            ]
        );
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }

    #[test]
    fn same_path_same_content_not_changed() {
        let (a, b) = dirs();
        fs::write(a.join("same.txt"), "hello\r\n").unwrap();
        fs::write(b.join("same.txt"), "hello\r\n").unwrap();
        assert!(compare_directories(&a, &b).unwrap().is_empty());
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }

    #[test]
    fn multiple_moved_identical_files_stay_ambiguous() {
        let (a, b) = dirs();
        for name in ["a.py", "b.py"] {
            fs::write(a.join(name), "same").unwrap();
        }
        for name in ["c.py", "d.py"] {
            fs::write(b.join(name), "same").unwrap();
        }
        let events = compare_directories(&a, &b).unwrap();
        assert_eq!(events.len(), 4);
        assert!(
            !events
                .iter()
                .any(|e| matches!(e, FileChange::Renamed { .. }))
        );
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }

    #[test]
    fn refuses_oversized_file() {
        let (a, b) = dirs();
        let file = fs::File::create(b.join("huge.bin")).unwrap();
        file.set_len(MAX_FILE_BYTES + 1).unwrap();
        assert!(compare_directories(&a, &b).is_err());
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn does_not_follow_symlink_directory() {
        use std::os::unix::fs::symlink;
        let (a, b) = dirs();
        fs::write(a.join("outside.txt"), "secret").unwrap();
        symlink(&a, b.join("outside-link")).unwrap();
        assert_eq!(
            compare_directories(&a, &b).unwrap(),
            vec![FileChange::Deleted("outside.txt".into())]
        );
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }
}
