# Text-to-Speech WebSocket Starter for .NET

This example app demonstrates how to use the Deepgram Text-to-Speech API over WebSockets with .NET.

The flow of this sample is:

1. A websocket is opened from the UI to the backend .NET component
1. Text is sent over a websocket to the backend component
1. If a connection has not been established to Deepgram, create a websocket connection using the Python SDK and send the text to convert to audio
1. An audio byte response with synthesized text-to-speech is returned and forward back through the WebSocket created by the UI
1. Those audio bytes are then played by the media device contained within your browser

<img src="/public/assets/preview-starter.png" alt="A preview of the app" style="width: 400px; height: auto; border-radius: 10px; margin-top: 20px;">

## What is Deepgram?

[Deepgram](https://deepgram.com/) is a voice AI company providing speech-to-text and language understanding capabilities to make data readable and actionable by human or machines.

## Sign-up to Deepgram

Before you start, it's essential to generate a Deepgram API key to use in this project. [Sign-up now for Deepgram and create an API key](https://console.deepgram.com/signup?jump=keys).

## Quickstart

### Manual

Follow these steps to get started with this starter application.

#### Clone the repository

Navigate to GitHub and [clone the repository](https://github.com/deepgram-starters/dotnet-live-text-to-speech).

#### Install dependencies

Install the project dependencies.

```bash
dotnet add package Deepgram
```

#### Set your Deepgram API key

Set your `DEEPGRAM_API_KEY` environment variable in your user environment variables in the control panel.

#### Run the .NET Application

There are multiple ways of starting this application. If you are using Visual Studio, you can start a debug session. You can also build the application and run it from the command line.

#### Open the UI in a Browser

To open the Frontend UI, just navigate to `http://localhost:3000` in Chrome.

## Issue Reporting

If you have found a bug or if you have a feature request, please report them at this repository issues section. Please do not report security vulnerabilities on the public GitHub issue tracker. The [Security Policy](./SECURITY.md) details the procedure for contacting Deepgram.

## Getting Help

We love to hear from you so if you have questions, comments or find a bug in the project, let us know! You can either:

- [Open an issue in this repository](https://github.com/deepgram-starters/live-node-starter/issues/new)
- [Join the Deepgram Github Discussions Community](https://github.com/orgs/deepgram/discussions)
- [Join the Deepgram Discord Community](https://discord.gg/xWRaCDBtW4)

## Author

[Deepgram](https://deepgram.com)

## License

This project is licensed under the MIT license. See the [LICENSE](./LICENSE) file for more info.
