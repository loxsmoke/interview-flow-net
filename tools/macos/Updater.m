#import <Cocoa/Cocoa.h>
#import <Sparkle/Sparkle.h>

// Retain the controller for the process lifetime. Called on Avalonia's UI thread
// after the main window opens, when AppKit has finished launching.
static SPUStandardUpdaterController *controller;

__attribute__((visibility("default"))) int if_updater_start(void)
{
    @autoreleasepool {
        if (![NSThread isMainThread]) return 0;
        if (controller != nil) return 1;
        controller = [[SPUStandardUpdaterController alloc]
            initWithStartingUpdater:NO updaterDelegate:nil userDriverDelegate:nil];
        NSError *error = nil;
        if (![controller.updater startUpdater:&error]) {
            NSLog(@"Interview Flow updater: %@", error);
            controller = nil;
            return 0;
        }
        return 1;
    }
}

__attribute__((visibility("default"))) void if_updater_check(void)
{
    @autoreleasepool {
        if ([NSThread isMainThread] && controller.updater.canCheckForUpdates)
            [controller checkForUpdates:nil];
    }
}
