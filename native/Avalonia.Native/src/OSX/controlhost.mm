#include "common.h"


static ComPtr<IAvnNativeControlHostTopLevelAttachment> CreateAttachment();

class AvnNativeControlHost :
    public ComSingleObject<IAvnNativeControlHost, &IID_IAvnNativeControlHost>
{
public:
    FORWARD_IUNKNOWN();
    NSView* View;
    AvnNativeControlHost(NSView* view)
    {
        View = view;
    }
    
    virtual HRESULT CreateDefaultChild(void* parent, void** retOut) override
    {
        @autoreleasepool
        {
            NSView* view = [NSView new];
            [view setWantsLayer: true];
            
            *retOut = (__bridge_retained void*)view;
            return S_OK;
        }
    };
    
    virtual ComPtr<IAvnNativeControlHostTopLevelAttachment> CreateAttachment() override
    {
        return ::CreateAttachment();
    };
    
    virtual void DestroyDefaultChild(void* child) override
    {
        // ARC will release the object for us
        #pragma clang diagnostic push
        #pragma clang diagnostic ignored "-Wunused-value"
        (__bridge_transfer NSView*) child;
        #pragma clang diagnostic pop
    }
};

class AvnNativeControlHostTopLevelAttachment :
public ComSingleObject<IAvnNativeControlHostTopLevelAttachment, &IID_IAvnNativeControlHostTopLevelAttachment>
{
    NSView* _holder;
    NSView* _child;
    // Number of the latest ShowInBounds or HideWithSize call. A call deferred to the main queue
    // applies only while it is still the latest, so it cannot replace a later placement.
    int _placement;

    void ShowInBounds(int placement, float x, float y, float width, float height)
    {
        if(_child == nil || placement != _placement)
            return;
        if(AvnInsidePotentialDeadlock::IsInside())
        {
            auto slf = comself(this);
            dispatch_async(dispatch_get_main_queue(), ^{
                slf->ShowInBounds(placement, x, y, width, height);
            });
            return;
        }

        NSRect childFrame = {0, 0, width, height};
        NSRect holderFrame = {x, y, width, height};

        [_child setFrame: childFrame];
        [_holder setFrame: holderFrame];
        [_holder setHidden: false];
        if([_holder superview] != nil)
            [[_holder superview] setNeedsDisplay:true];
    }

    void HideWithSize(int placement, float width, float height)
    {
        if(_child == nil || placement != _placement)
            return;
        if(AvnInsidePotentialDeadlock::IsInside())
        {
            auto slf = comself(this);
            dispatch_async(dispatch_get_main_queue(), ^{
                slf->HideWithSize(placement, width, height);
            });
            return;
        }

        NSRect frame = {0, 0, width, height};
        [_holder setHidden: true];
        [_child setFrame: frame];
    }
public:
    FORWARD_IUNKNOWN();
    
    AvnNativeControlHostTopLevelAttachment()
    {
        _holder = [NSView new];
        [_holder setWantsLayer:true];
        _placement = 0;
    }
    
    virtual ~AvnNativeControlHostTopLevelAttachment()
    {
        if(_child != nil && [_child superview] == _holder)
        {
            [_child removeFromSuperview];
        }
        
        if([_holder superview] != nil)
        {
            [_holder removeFromSuperview];
        }
    }
    
    virtual void* GetParentHandle() override
    {
        return (__bridge void*)_holder;
    };
    
    virtual HRESULT InitializeWithChildHandle(void* child) override
    {
        @autoreleasepool
        {
            if(_child != nil)
                return E_FAIL;
            _child = (__bridge NSView*)child;
            if(_child == nil)
                return E_FAIL;
            [_holder addSubview:_child];
            [_child setHidden: false];
            return S_OK;
        }
    };
    
    virtual HRESULT AttachTo(const ComPtr<IAvnNativeControlHost>& host) override
    {
        @autoreleasepool
        {
            if(host == nullptr)
            {
                [_holder removeFromSuperview];
                [_holder setHidden: true];
            }
            else
            {
                auto chost = host.dynamicCast<AvnNativeControlHost>();
                if(chost == nil || chost->View == nil)
                    return E_FAIL;
                [_holder setHidden:true];
                [chost->View addSubview:_holder];
            }
            return S_OK;
        }
    };
    
    virtual void ShowInBounds(float x, float y, float width, float height) override
    {
        ShowInBounds(++_placement, x, y, width, height);
    }
    
    virtual void HideWithSize(float width, float height) override
    {
        HideWithSize(++_placement, width, height);
    }
    
    virtual void ReleaseChild() override
    {
        [_child removeFromSuperview];
        _child = nil;
    }
};

static ComPtr<IAvnNativeControlHostTopLevelAttachment> CreateAttachment()
{
    return comnew<AvnNativeControlHostTopLevelAttachment>();
}

extern ComPtr<IAvnNativeControlHost> CreateNativeControlHost(NSView* parent)
{
    return comnew<AvnNativeControlHost>(parent);
}
