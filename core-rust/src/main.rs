use documentcompare_core::diff_lines;
use std::{env, fs, process};

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() != 3 {
        eprintln!("Usage: dc-code-diff <before-utf8-file> <after-utf8-file>");
        process::exit(2);
    }
    let old = fs::read_to_string(&args[1]).unwrap_or_else(|e| {
        eprintln!("Failed to open before file: {e}");
        process::exit(1);
    });
    let new = fs::read_to_string(&args[2]).unwrap_or_else(|e| {
        eprintln!("Failed to open after file: {e}");
        process::exit(1);
    });
    for change in diff_lines(&old, &new) {
        println!(
            "{:?}\tbefore:{}-{}\tafter:{}-{}",
            change.kind,
            change.before.start + 1,
            change.before.end,
            change.after.start + 1,
            change.after.end
        );
    }
}
