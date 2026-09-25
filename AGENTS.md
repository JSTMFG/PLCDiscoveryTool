# Repository delivery

After completing requested updates to this repository, validate the changes, commit only files related to the request, and push the working branch to GitHub. The user has asked for this to be the normal completion step. Preserve unrelated local changes, respect explicit requests for local-only work, and follow branch protections.

Build Windows executables with `build.ps1`. Keep generated files in `dist` out of source commits; use a GitHub release when publishing downloadable binaries.
