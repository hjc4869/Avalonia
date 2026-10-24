#import <AppKit/AppKit.h>
#import <objc/message.h>
#import <objc/runtime.h>

__attribute__((constructor(101)))
static void AvaloniaNativeInitializeFonts(void)
{
    @autoreleasepool
    {
        [NSApplication sharedApplication];
        [NSFont systemFontOfSize:13.0];
        SEL selector = sel_registerName("systemFontOfSize:width:");
        if ([NSFont respondsToSelector:selector])
            ((id (*)(id, SEL, CGFloat, CGFloat))objc_msgSend)([NSFont class], selector, 13.0, 0.0);
    }
}