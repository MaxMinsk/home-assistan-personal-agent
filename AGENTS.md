## Project Workflow

- Do not bump `addon/config.yaml` version unless the user explicitly asks for a release/version bump.
- Do not create release commits or tags unless the user explicitly asks for a release.
- Do not run or trigger Home Assistant add-on image/release builds unless the user explicitly asks for it.
- Do not push to `main` just to trigger the Home Assistant add-on image build unless the user explicitly asks for it.
- Local build, test, and format checks are allowed for implementation verification.
- Because this project is learning-first, every C# class, record, and interface should have a Russian XML documentation header that explains what it is, why it exists, and how it works.
